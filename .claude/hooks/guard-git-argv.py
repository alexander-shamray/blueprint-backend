#!/usr/bin/env python3
"""Judge git on the argv the shell will execute, not on the string a caller types.

A permission rule matches the typed string and the shell executes an argv.
`.claude/settings.json` denies `Bash(git *--output*)`, which closes the naive
spelling and nothing more: the shell reassembles adjacent quoted fragments
before `exec`, so `--out''put=<path>` reaches git as `--output=<path>` while
never presenting the matcher with a contiguous `--output`.
`docs/harness-boundaries.md` states that limit and names a rule over the
executed argv as its fix, which is this file.

It also closes one thing the rule system cannot express. `Bash(git *ext::*)`
passes settings validation and then matches nothing — the trailing `:*` is
consumed as the prefix-wildcard form — while `Bash(git *ext::**)` is rejected
at startup. So `ext::`, a git transport that runs its argument as a command,
has no expressible Bash deny. It has one here.

The push half is an allow-list, because a deny-list of dangerous spellings
trails the grammar: `--force-with-lease=<ref>`, the abbreviation `--for`, the
bundle `-fv`, `--all` and `--mirror` with no refspec to inspect, a wildcard
destination that includes `main`, and `git push origin HEAD`, which names no
destination at all, each walk past one. So a push is refused unless every part
of it is recognised: one remote, one refspec that names a destination, and
options drawn from a fixed set.

Three things the parser has to do before it can judge anything:

  * heredoc bodies are data. `shlex` knows nothing about them, so a commit
    body would be tokenised as arguments. Stripped first.
  * operators without spaces still separate commands. `shlex.split` leaves
    `--oneline&&git` as one element, so `git log --oneline&&git push origin
    +HEAD:main` would never start a second segment.
    `punctuation_chars=True` separates it and leaves quoted content alone.
  * a command substitution is executed, not quoted away. `git log "$(git
    push origin +HEAD:main)"` is one `shlex` token and two commands to the
    shell. Substitutions are extracted and judged in their own right.

What a value-taking flag is depends on the subcommand: `-m` takes a value for
`commit` and none for `log`, so a global skip-list would let
`git log -m --out''put=<path> --format=%B` walk the skipped element past the
check. The map below is consulted per subcommand and skips nothing by default,
because not skipping costs a false positive and skipping wrongly costs a
bypass.

The residuals: `shlex` resolves quoting and command substitution is handled,
but not expansion — a flag assembled at run time,
`F=--output=x; git log $F`, arrives as the token `$F`, and closing that needs
the argv after expansion, which no hook is given. And the value-flag map
trails git's options the way any list does; it is load-bearing for false
positives, never for a bypass.

Protocol: PreToolUse, matcher `Bash`. Exit 0 and print nothing to allow; print
the deny JSON to refuse. Exit 2 would also block, but the JSON form carries a
reason the caller can read, and a guard that refuses without saying why is one
that gets worked around rather than fixed.
"""

import fnmatch
import functools
import json
import os
import re
import shlex
import sys
import tempfile
import traceback
from bisect import bisect_left

# Flags that write or execute rather than inspect. Matched on a prefix, so
# `--exec-path=<dir>` — a directory of binaries for git to run — is the same act
# as `--exec`.
FORBIDDEN_FLAGS = ("--output", "--upload-pack", "--receive-pack", "--exec",
                   "--ext-diff", "--textconv", "--extcmd",
                   "--open-files-in-pager")

# Options of their own that the abbreviation test would read as a forbidden
# flag cut short: `git diff --text` is not `--textconv`.
WHOLE_OPTIONS = {"--text"}

# Judged against a whole element, and only on a subcommand that takes a
# repository — a branch name, a path or a commit body may carry the sequence
# without using it as a transport.
FORBIDDEN_SUBSTRINGS = ("ext::",)

# `git -c <key>=<value>` sets configuration for one invocation, and many config
# keys are executed by git — `alias.*`, `core.pager`, `core.sshCommand`,
# `diff.external`, `filter.*.clean` and `credential.helper` among them — so
# `git -c "alias.x=!cmd" x` runs `cmd`. A list of the executing keys grows on
# git's schedule, so the option is refused instead of its values being judged;
# nothing in this repository passes `-c` or `--config-env` to git. A caller
# that needs one wants an allow-list of keys.
CONFIG_OPTIONS = ("-c", "--config-env")

# Subcommands that exist to run something: the flag lists trail git, so these
# are refused whole. Everything else stays admitted, as an allow-list would not.
REFUSED_SUBCOMMANDS = {
    "filter-branch": "runs the shell commands its --*-filter options carry",
    "send-email": "runs the programs its --*-cmd and --smtp-server options name",
    "instaweb": "starts the web server its --httpd option names",
    "daemon": "starts a server that runs the services it is told to",
    "http-backend": "is a CGI program that runs git for whoever calls it",
    "web--browse": "starts the browser program it is given",
    "difftool": "runs the program `diff.tool` names, the `--ext-diff` act by another verb",
    "mergetool": "runs the program `merge.tool` names, the `--ext-diff` act by another verb",
    "send-pack": "updates a remote ref past every check a `git push` gets",
    "http-push": "updates a remote ref past every check a `git push` gets",
    "receive-pack": "takes a push into a repository past every check a `git push` gets",
}
REPOSITORY_SUBCOMMANDS = {
    "fetch", "clone", "pull", "push", "remote", "submodule", "ls-remote",
    "archive", "bundle",
}

# The short spelling of a flag that runs a command, by subcommand, because a
# letter means something else on another: `cherry-pick -x` records a trailer.
# `archive -o` and `format-patch -o` only write a named file, so stay admitted.
EXECUTING_SHORT_FLAGS = {
    "rebase": "xs", "pull": "s", "clone": "u", "ls-remote": "u", "grep": "O",
}

# A flag that runs something on one subcommand only: `commit --template` only
# reads a file. Matched whole or abbreviated, so `--strategy-option` is free.
EXECUTING_LONG_FLAGS = {
    "clone": ("--template",), "init": ("--template",),
    "pull": ("--strategy",), "rebase": ("--strategy",),
}

# Subcommands that run the operand they are handed, which no flag check sees.
EXECUTING_FORMS = {"bisect": "run", "submodule": "foreach",
                   "submodule--helper": "foreach"}

# Letters after which the rest of a word is a value, so `-Xours` holds no `-s`.
GLUED_VALUE_LETTERS = {
    "rebase": "X", "pull": "Xo", "clone": "bcjo", "grep": "efABCm", "fetch": "jo",
}

# Git's own options, which sit before the subcommand, taken from git's synopsis.
# The list trails git's globals; it is load-bearing only for locating a
# subcommand, never for the push check, which does not ask where the subcommand
# is.
GLOBAL_VALUE_FLAGS = {
    "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env",
    "--attr-source",
}

# Per subcommand, because arity is not a property of a flag name: `-m` is a
# message for `commit` and "show merge diffs" for `log`. Absent an entry, nothing
# is skipped — a false positive is cheap and a bypass is not.
VALUE_FLAGS_BY_SUBCOMMAND = {
    "commit": {"-m", "--message", "-F", "--file", "-C", "--reuse-message",
               "-c", "--reedit-message", "--author", "--date", "--squash",
               "--fixup", "--pathspec-from-file"},
    "tag": {"-m", "--message", "-F", "--file", "-u", "--local-user"},
    "merge": {"-m", "--message", "-F", "--file", "-S", "--strategy"},
    "stash": {"-m", "--message"},
    "notes": {"-m", "--message", "-F", "--file"},
    "revert": {"-m", "--mainline", "-S"},
    "cherry-pick": {"-m", "--mainline", "-S"},
    "branch": {"-u", "--set-upstream-to", "--contains", "--sort"},
    "grep": {"-e", "-f", "-A", "-B", "-C", "-m"},
}

SEPARATORS = {"&&", "||", ";", "|", "&", "(", ")", "{", "}", "\n"}

# ---- what a target protects ------------------------------------------------

SETTINGS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                        "settings.json")
EDIT_RULE = re.compile(r"^Edit\((?P<path>.*)\)$")


@functools.lru_cache(maxsize=1)
def edit_denies():
    """This target's `Edit(...)` deny paths, root-relative and folded.

    `.claude/settings.json` is the one list, so `.github` is protected only
    where a target denies it; `/x` is the root's, and `//x` and `~/x` no path.
    """
    with open(SETTINGS, encoding="utf-8") as handle:
        rules = json.load(handle).get("permissions", {}).get("deny", [])
    paths = [EDIT_RULE.match(rule) for rule in rules]
    folded = (match.group("path").lower() for match in paths if match)
    return tuple(dict.fromkeys(
        path[2:] if path.startswith("./") else path.removeprefix("/")
        for path in folded if not path.startswith(("//", "~"))))


def denied_path(relative, whole=False):
    """The deny that holds `relative`, a path from a checkout's root, or `None`.

    Anchored at the root and folded. With `whole`, a parent of a deny's prefix counts.
    `fnmatchcase` wants a `/` before `**/`, so such a rule is asked again without it.
    """
    parts = [part for part in re.split(r"[\\/]+", relative)
             if part not in ("", ".")]
    path = "/".join(parts).lower()
    if not path or path == ".." or path.startswith("../"):
        return None
    for rule in edit_denies():
        literal = re.split(r"[*?\[]", rule, maxsplit=1)[0].rstrip("/")
        if fnmatch.fnmatchcase(path, rule) or (
                rule.startswith("**/") and fnmatch.fnmatchcase(path, rule[3:])) or (
                rule.endswith("/**") and path == rule[:-3]) or (
                whole and literal.startswith(path + "/")):
            return f"`Edit({rule})`"
    return None


# ---- the push allow-list ---------------------------------------------------

# Options a push may carry. Anything else — including a spelling git invented
# last week, an abbreviation, or a bundle like `-fv` — is refused rather than
# checked against a list of what is dangerous.
PUSH_ALLOWED_FLAGS = {
    "-u", "--set-upstream", "-q", "--quiet", "-v", "--verbose",
    "--porcelain", "--progress", "--no-progress", "--atomic", "--no-verify",
    "--follow-tags", "-n", "--dry-run",
}

# A ref this guard is willing to read: no `*`, no `+`, no `:` beyond the one
# separator, nothing that could be a pattern or an option. Parentheses form
# none of those, and /branch's `feat(<scope>)/` names carry them.
SAFE_REF = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._/()-]*$")
SAFE_REMOTE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")

# Sources that name no destination of their own. `git push origin HEAD` updates
# whatever branch you are standing on — `main`, if you are on `main` — and a
# hook is not given the repository state to find out which.
UNRESOLVABLE_SOURCES = {"HEAD", "@", "HEAD~", "@{u}", "@{upstream}"}

PROTECTED_BRANCHES = {"main"}

# Heredoc introducers. The body between the introducer and its delimiter is data
# the shell hands to a command, not a command line.
#
# A delimiter is a shell word, not an identifier-shaped prefix of one: bash
# terminates `<<EOF-1` at an `EOF-1` line, so reading `EOF` would take the whole
# tail, and a push after it, for an unterminated body. `[ \t]*` rather than
# `\s*`, because a newline between `<<` and its delimiter is not a heredoc to
# bash either. The quote characters are spelled \x27 and \x22 so that neither
# this pattern nor anything quoting it has to escape them.
#
# The word is one or more fragments — single-quoted, double-quoted, `$'…'` or
# `$"…"`, escaped, a line continuation, or bare — because bash reads
# `<<E"OF"`, `<<$'EOF'` and `<<EO\<newline>F` all as `EOF`, and a delimiter
# read short or long shifts the body over a command line such as
# `git <<E"OF" push origin +HEAD:main`. `\\\n` leads the alternatives because
# `\\.` cannot match a newline; `join_continuations` cannot help, because
# `strip_heredocs` runs on the raw command before it. A quoted fragment ends at
# an unescaped quote and never spans a line, or a planted line could close the
# body. `_heredoc_delimiter` below does the quote removal the shell does.
HEREDOC = re.compile(
    r"<<(?P<dash>-?)[ \t]*"
    r"(?P<word>(?:\\\n"
    r"|\$?\x27[^\x27\n]*\x27"
    r"|\$?\x22(?:[^\x22\\\n]|\\[^\n])*\x22"
    r"|\\[^\n]"
    r"|[^\s;&|<>()\x27\x22\\])+)"
)


def _sigil_quote(word, index):
    """Where the quote of a `$'…'`/`$"…"` at `index` starts, or None.

    Line continuations between the two are skipped, because bash removes them
    before it reads the word.
    """
    peek = index + 1
    while word[peek:peek + 2] == "\\\n":
        peek += 2
    return peek if word[peek:peek + 1] in ("'", '"') else None


def _heredoc_delimiter(word):
    """The literal delimiter `word` names, and whether its body expands.

    Bash removes the quoting from a heredoc delimiter and expands the body only
    when the word carried no quoting at all — and the quoting may be partial,
    which is why this is a function rather than a group in the pattern.
    `<<E"OF"`, `<<"EOF"`, `<<'EOF'` and `<<\\EOF` all name `EOF` and all take
    their bodies verbatim; only a wholly bare `<<EOF` expands.

    A delimiter this cannot read exactly — an ANSI-C fragment whose escapes
    need decoding, or a locale-quoted one, which is translated — is returned
    as `None`, and `heredoc_spans` then opens no body at all, leaving every
    following line to be judged as the command it may be. A delimiter read
    too long swallows the commands after it, so refusing is the direction that
    costs a false positive rather than a force push.

    The pattern admits a fragment only in complete form, so every quote opened
    here is closed and the searches below cannot fail.
    """
    out, index, quoted = [], 0, False
    while index < len(word):
        char = word[index]
        if char == "$" and _sigil_quote(word, index) is not None:
            # A continuation between the sigil and its quote does not break
            # the pairing, because bash removes the pair before it reads the
            # word: `<<$\<newline>'EOF'` names `EOF`, not `$EOF`.
            peek = _sigil_quote(word, index)
            quote = word[peek]
            close = word.index(quote, peek + 1)
            body = word[peek + 1:close]
            if quote == '"':
                # A locale-quoted delimiter is translated, as a locale-quoted
                # word is (`undecodable_dollar_quote`): a catalogue can end
                # the body on a different line from the literal reading, and
                # a push between the two is swallowed or exposed.
                return None, False
            # An ANSI-C body with a backslash needs its escapes decoded, and a
            # guessed delimiter can let a line the payload plants close the
            # body early or late.
            if "\\" in body:
                return None, False
            out.append(body)
            index = close + 1
            quoted = True
            continue
        if char == "'":
            close = word.index(char, index + 1)
            out.append(word[index + 1:close])
            index = close + 1
            quoted = True
            continue
        if char == '"':
            # Double quotes carry escapes, so the closer is the first unescaped
            # one and `\"` contributes a quote rather than ending the fragment.
            scan, body = index + 1, []
            while scan < len(word):
                if word[scan] == "\\" and scan + 1 < len(word):
                    body.append(word[scan + 1] if word[scan + 1] in '$`"\\'
                                else word[scan:scan + 2])
                    scan += 2
                    continue
                if word[scan] == '"':
                    break
                body.append(word[scan])
                scan += 1
            out.append("".join(body))
            index = scan + 1
            quoted = True
            continue
        if char == "\\" and word[index + 1:index + 2] == "\n":
            # A continuation inside the delimiter contributes nothing and
            # quotes nothing — bash removes the pair before it reads the word.
            index += 2
            continue
        if char == "\\" and index + 1 < len(word):
            out.append(word[index + 1])
            index += 2
            quoted = True
            continue
        out.append(char)
        index += 1
    return "".join(out), not quoted


def shell_positions(command, data=()):
    """Walk `command`, yielding `(index, in_quotes, in_comment)` per character.

    One of two adapters over `_quoting`, which is where the model lives.
    """
    for index, state, _escaped in _quoting(command, data=data):
        yield index, state in ("single", "double", "data"), state == "comment"


def quote_states(command, quotes=True):
    """`command`'s quoting state at each character.

    `state[i]` is `"single"`, `"double"`, `"comment"` or `""`. A scanner that
    also needs to know where an escape sits keeps its own backslash branch:
    every caller here already had one, and they differ — a continuation join
    deletes the pair, an expansion rewrite copies it through.

    The scanners in this file read bash's quote rules through here rather
    than each carrying a copy, because a copy that misses a form — `$'…'` —
    leaves its pass one quote out of step, and `$( )`, `${x:-push}`, a line
    continuation or a nested `$(git push …)` then walks past the pass that
    exists to catch it.

    `quotes` is false for a heredoc body, where a quote is an ordinary
    character — the same flag its callers already take.
    """
    states = [""] * len(command)
    for index, state, _escaped in _quoting(command, quotes=quotes):
        states[index] = state
    return states


def _quoting(command, quotes=True, data=(), start=0):
    """Yield `(index, state, escaped)` per character of `command`.
    `data` is spans read as data: `heredoc_spans`, `_sums_in_group` pass it."""
    # Not copied and not sorted: `heredoc_spans` appends to this list
    # while consuming the generator, and every span it appends starts ahead of
    # the cursor, so the order holds by construction.
    single = double = comment = False
    # Whether the single quote now open was introduced by a `$`, and whether
    # the character just yielded was an unquoted, unescaped `$`.
    ansi_c = dollar = False
    # Whether a `#` begins a word, tracked rather than inferred from the
    # previous character, which cannot tell a separating space from an escaped
    # one: in `git log --grep=foo\\ #bar;git push origin +HEAD:main` bash keeps
    # `#bar` inside the `--grep` argument and runs the push.
    at_word_start = True
    index = start  # Read as unquoted and as beginning a word.
    # Whether this character is the one the backslash before it escapes.
    pending = False
    # A cursor rather than a search: this walk is monotonic, so the spans are
    # consumed in order, and a per-character search is quadratic in heredocs.
    cursor = 0
    while index < len(command):
        while cursor < len(data) and data[cursor][1] <= index:
            cursor += 1
        if cursor < len(data) and data[cursor][0] <= index:
            while index < data[cursor][1] and index < len(command):
                yield index, "data", False
                index += 1
            at_word_start = True
            dollar = False
            continue
        char = command[index]
        if comment:
            if char == "\n":
                comment = False
                at_word_start = True
            else:
                yield index, "comment", False
                index += 1
                continue
        if not comment:
            if single:
                if ansi_c and char == "\\" and index + 1 < len(command):
                    # Both characters, for `strip_comments`' reason below: a
                    # consumer rebuilds text from these positions.
                    yield index, "single", False
                    yield index + 1, "single", True
                    index += 2
                    dollar = False
                    continue
                if char == "'":
                    single = ansi_c = False
            elif double:
                if char == "\\" and index + 1 < len(command):
                    # Both characters, because a consumer rebuilds text from
                    # these positions: yielding only the backslash would make
                    # `strip_comments` delete the escaped character, and every
                    # later stage would inherit the edit.
                    yield index, "double", False
                    yield index + 1, "double", True
                    index += 2
                    dollar = False
                    continue
                if char == '"':
                    double = False
            elif char == "\\" and index + 1 < len(command):
                # An unquoted backslash escapes the next character, so that
                # character is ordinary text — a space included, and an escaped
                # space separates nothing.
                yield index, "", False
                yield index + 1, "", True
                index += 2
                at_word_start = False
                dollar = False
                continue
            elif char == "'" and quotes:
                single = True
                ansi_c = dollar
                at_word_start = False
            elif char == '"' and quotes:
                double = True
                at_word_start = False
            elif char == "#" and at_word_start:
                comment = True
                yield index, "comment", False
                index += 1
                dollar = False
                continue
            elif char in METACHARACTERS:
                at_word_start = True
            else:
                at_word_start = False
        yield index, ("single" if single else "double" if double
                      else "comment" if comment else ""), pending
        pending = False
        dollar = char == "$" and not (single or double or comment)
        index += 1


def _opened_body(command, index, pending):
    """`(start, end, expands)` for a `<<` at `index`, past `pending`: None if no body, `end` None if none closes it."""
    # `<<<` is a here-string: `cat <<<EOF` passes the word `EOF` on stdin and the
    # next line is an ordinary command, so matching `<<EOF` at its second
    # character would swallow that line. Two tests, because the operator has two
    # ends: an index inside a run of `<`, and an operator continuing past `<<`.
    if index > 0 and command[index - 1] == "<":
        return None
    if command.startswith("<<<", index):
        return None
    match = HEREDOC.match(command, index)
    if not match:
        return None
    # One parse of the delimiter word, quote removal included —
    # `<<\EOF` is a quoted delimiter to bash the same way `<<'EOF'`
    # is, and `<<E"OF"` is one in parts.
    delimiter, expands = _heredoc_delimiter(match.group("word"))
    if delimiter is None:
        # A delimiter this file cannot decode opens no body, so the
        # lines after it stay commands and are judged as such.
        return None
    intro_end, dash = match.end(), bool(match.group("dash"))

    # An introducer inside an earlier body is body text, which the scanner
    # settles by not walking a body at all. Two heredocs stacked on one
    # line both introduce before either body starts, which is `pending`'s
    # job below.

    # A body begins on the next line: everything between the introducer
    # and that newline is still command line, as the push in
    # `cat <<'A' ; git push origin +HEAD:main` is.
    newline = command.find("\n", intro_end)
    if newline == -1:
        # An introducer with no line after it opens no body at all.
        return None

    # Stacked bodies queue: the second starts where the first terminated,
    # which is past its own line break.
    start = max(newline + 1, pending)
    # The terminator is the delimiter and nothing else: bash accepts no
    # indented or trailing-spaced terminator — only `<<-` strips leading
    # tabs — so a commit body that indents the word keeps going.
    terminator = (
        rf"^\t*{re.escape(delimiter)}$" if dash
        else rf"^{re.escape(delimiter)}$")
    closing = re.search(terminator, command[start:], re.MULTILINE)
    return start, None if closing is None else start + closing.end(), expands


def _closes_inside_backtick(command, index, end):
    """Whether a body ending at `end` does so before the backtick around `index` closes."""
    return end is not None and command.find("`", index) > end


def _sums_in_group(command, start):
    """Whether each `(` of the group opening at `start` opens a sum, and its end."""
    # As bash decides a `((`: quotes are read, and a heredoc only where a `$(…)`
    # or a backtick runs commands. A sum closes on `))`: `((a) | b)` is two
    # subshells. One pass over a stack, because `_closing_paren` per sum is
    # quadratic and a timeout fails open.
    verdicts, stack, previous, end = {}, [], None, len(command)
    substitutions, doubled, ticked, unsettled = [], [], False, False
    bodies, pending, hunting = [], 0, True
    for index, state, escaped in _quoting(command, data=bodies, start=start):
        if state or escaped:
            continue
        if command[index] == "(":
            if stack and stack[-1] == index - 1:
                doubled.append(index - 1)
            stack.append(index)
            if (command[index - 1:index] == "$"
                    and command[index + 1:index + 2] != "("):
                substitutions.append(index)
        elif command[index] == ")":
            opener = stack.pop()
            verdicts[opener] = previous == (opener + 1, index - 1)
            previous = (opener, index)
            for parens in (substitutions, doubled):
                if parens and parens[-1] == opener:
                    parens.pop()
            if not stack:
                end = index + 1
                break
        elif command[index] == "`":
            ticked = not ticked
        elif command.startswith("<<", index) and (ticked or substitutions):
            if not hunting or (not ticked and doubled and doubled[-1] > substitutions[-1]):
                unsettled = True
                continue
            body = _opened_body(command, index, pending)
            if body is None:
                continue
            # With no close in reach, the next `<<` would search to the end too.
            hunting = body[1] is not None and (
                not ticked or _closes_inside_backtick(command, index, body[1]))
            if hunting:
                pending = body[1]
                bodies.append(body[:2])
    verdicts.update(dict.fromkeys(stack, False))
    # A `<<` this read could not settle may hide a close. A sum opens no body, so
    # the lines after stay commands, and the cost is a false refusal.
    return dict.fromkeys(verdicts, True) if unsettled else verdicts, end


class _Sums:
    """Which `<<` are shifts, for the walk `heredoc_spans` makes.

    A group is read ahead only where a `<<` needs the verdict, and no two reads
    overlap, so the walk stays linear.
    """

    def __init__(self, command):
        self.command = command
        # The parens open at the walk, those a `$` leads, and each `((` among
        # them not yet refused, named by its outer paren. All three ascend.
        self.opened, self.substitutions, self.pairs = [], [], []
        self.verdicts, self.read_to, self.backtick, self.bodiless_to = {}, 0, None, 0

    def open(self, index):
        if self.opened and self.opened[-1] == index - 1:
            self.pairs.append(index - 1)
        if self.command[index - 1:index] == "$":
            self.substitutions.append(index)
        self.opened.append(index)

    def close(self):
        if not self.opened:
            return
        index = self.opened.pop()
        for parens in (self.substitutions, self.pairs):
            if parens and parens[-1] == index:
                parens.pop()

    def tick(self, index):
        self.backtick = index if self.backtick is None else None

    def is_shift(self, index, pending):
        """Whether a `<<` at `index` is in a sum and opens no body."""
        pairs, substitutions = self.pairs, self.substitutions
        while pairs:
            if pairs[-1] >= self.read_to:
                first = pairs[bisect_left(pairs, self.read_to)]
                verdicts, self.read_to = _sums_in_group(self.command, first)
                self.verdicts.update(verdicts)
            # A read that did not see this paren is one a body's lone quote put
            # out of step, which is a syntax error in bash too. Reading a shift
            # opens no body, so the lines after it stay commands.
            if self.verdicts.get(pairs[-1], True):
                if self.backtick is None or self.backtick < pairs[-1]:
                    return not substitutions or substitutions[-1] <= pairs[-1]
                return not self._backtick_holds_body(index, pending)
            pairs.pop()
        return False

    def _backtick_holds_body(self, index, pending):
        """Whether a `<<` at `index` closes its body before its backtick closes."""
        if index < self.bodiless_to:
            return False
        body = _opened_body(self.command, index, pending)
        if body is None:
            return False
        # A backtick's heredoc ends with its text, so the lines after that are
        # commands. Once per backtick, or each `<<` in it searches to the end.
        if not _closes_inside_backtick(self.command, index, body[1]):
            close = self.command.find("`", index)
            self.bodiless_to = len(self.command) if close == -1 else close
            return False
        return True


def heredoc_spans(command, unclosed=None, openers=None):
    """Every heredoc body in `command`, as `(start, end, expands)`.

    `start` is just past the introducer and `end` just past the closing
    delimiter line, so `command[start:end]` is everything the shell hands over
    as data rather than reading as a command line.

    `expands` matters: `<<'EOF'` and `<<"EOF"` hand the body over verbatim,
    while a bare `<<EOF` performs substitution and parameter expansion on it
    first. A guard that treats both as inert misses a live
    `$(git push origin +HEAD:main)` in the second, and one that treats both as
    executable refuses an honest commit quoting one in the first.

    An opener is only an opener in executable position. A `<<EOF` inside a
    comment or inside quotes is text, and treating it as an operator would let
    `git status # <<EOF` delete the command on the following line.
    """
    # A body is data, and its quotes are not the command line's: an apostrophe
    # in one would open a quote running to the end of the command, so every
    # later opener would sit `in_quotes` and its body be tokenised as commands.
    #
    # `data` is handed to the scanner and appended to while it walks, which is
    # what makes this one pass; feeding spans back between whole passes
    # recovers one body per pass, which is quadratic, and a timeout here fails
    # open. The scanner consumes `data` through a cursor and this loop only
    # appends spans that start ahead of it, so the list is sorted by
    # construction and the walk stays monotonic.
    data, spans, pending = [], [], 0
    sums = _Sums(command)
    for index, state, escaped in _quoting(command, data=data):
        if state:
            continue
        if not escaped and command[index] == "(":
            sums.open(index)
        elif not escaped and command[index] == ")":
            sums.close()
        elif not escaped and command[index] == "`":
            sums.tick(index)
        if not command.startswith("<<", index) or sums.is_shift(index, pending):
            continue
        body = _opened_body(command, index, pending)
        if body is None:
            continue
        start, end, expands = body
        if end is None:
            # No span, so nothing is stripped. A delimiter this guard cannot
            # find means either an unterminated heredoc, whose tail is data and
            # over-refuses, or a delimiter read wrongly, whose tail holds
            # commands. Scanning the tail is wrong only in the safe direction.
            if unclosed is not None:
                unclosed.append(index)
            break
        pending = end
        spans.append((start, pending, expands))
        data.append((start, pending))
        # One for one with `spans`: a reader pairing bodies with openers takes
        # them here, since re-deriving the list disagrees with this scan's skips.
        if openers is not None:
            openers.append(index)
    return spans


def strip_heredocs(command):
    """`command` with every heredoc body removed, delimiters included.

    A heredoc body is an argument, and parsing it as a command line refuses an
    honest commit that quotes a push. The introducer is left in place so the
    rest of the line still tokenises.
    """
    out, cursor = [], 0
    for start, end, _expands in heredoc_spans(command):
        out.append(command[cursor:start])
        cursor = end
    out.append(command[cursor:])
    return "".join(out)


def strip_comments(command):
    """`command` with every shell comment removed, newlines kept.

    Bash's rule, not `shlex`'s: `shlex.shlex` honours `#` at any character
    position, so the push in `git log --grep=#x ; git push origin +HEAD:main`
    would vanish with the rest of the line, while bash starts a comment only
    where `#` begins a word. The lexer's comment handling is switched off in
    `offence` and this runs instead.
    """
    return "".join(
        command[index]
        for index, _in_quotes, in_comment in shell_positions(command)
        if not in_comment
    )


def undecodable_heredoc(command):
    """Whether a heredoc names a delimiter this file cannot read, refused with a reason that says so."""
    # The bodies are computed first and handed to the scanner, as in
    # `heredoc_spans`: an apostrophe in an earlier body would leave the scanner
    # in quote state, so a later undecodable opener would look quoted.
    bodies = heredoc_spans(command)
    quoted = set()
    for index, in_quotes, in_comment in shell_positions(
            command, [(start, end) for start, end, _ in bodies]):
        if in_quotes or in_comment:
            quoted.add(index)
    # A `<<` inside a heredoc body is data, not an opener, so a body quoting
    # `<<$'E\\x4fF'` is not refused; `heredoc_spans` is what knows where a body
    # is, which is why it is asked above.
    for match in HEREDOC.finditer(command):
        index = match.start()
        if index in quoted:
            # A body is among the spans handed to the scanner above, so an
            # opener inside one arrives quoted and stops here, with no
            # containment test: that is quadratic in heredocs, and the hook's
            # timeout is empty stdout, which is non-blocking.
            continue
        if index > 0 and command[index - 1] == "<":
            continue
        if command.startswith("<<<", index):
            continue
        delimiter, _expands = _heredoc_delimiter(match.group("word"))
        if delimiter is None:
            return True
    return False


def expansion_end(command, start):
    """The end of the parameter expansion at `start`, or None if there is none.

    The special parameters are expansions too: `$@`, `$*` and `$!` are empty
    in the shell Claude Code runs commands in — no positional parameters, no
    background job — so `git $@push origin +HEAD:main` closes up into a force
    push, and `--out$@put=` and `ext$@::` reopen the other two checks the same
    way.

    `$#`, `$?`, `$$`, `$-` and `$0` are deliberately absent: each expands to
    something non-empty, so none of them can join two words.
    """
    if not command.startswith("$", start):
        return None
    if command.startswith("${", start):
        # A `#` straight after the brace is a length, and read afresh a comment.
        close = _closing_brace(command, start + (3 if command.startswith("#", start + 2) else 2))
        return None if close is None else close + 1
    if command[start + 1:start + 2] in ("@", "*", "!"):
        return start + 2
    scan = start + 1
    while scan < len(command) and (command[scan].isalnum()
                                   or command[scan] == "_"):
        scan += 1
    return scan if scan > start + 1 else None


def glued(command, start, end):
    """Whether `command[start:end]` touches other characters of its own word.

    A word boundary is whitespace, a metacharacter, or the end of the string —
    so an expansion standing alone as `$BRANCH` is not glued, and the `${x}` of
    `--out${x}put=` is. This is the whole of the line between an expansion
    whose emptiness closes a word up and one that simply supplies a value.

    A quote is not a boundary: `git $x'push' origin +HEAD:main` runs the push,
    because quoting ends no word in bash — `'pu'$x'sh'` is one word too.
    """
    def boundary(position):
        if position < 0 or position >= len(command):
            return True
        return command[position] in METACHARACTERS

    return not (boundary(start - 1) and boundary(end))


def without_substitutions(command):
    """`command` with every command substitution deleted rather than tokenised.

    A substitution that prints nothing leaves the words around it joined, and
    the dangerous string is literally in the source: `git $( )push origin
    +HEAD:main` runs the push, while `shlex(punctuation_chars=True)` emits `(`
    and `)` as their own tokens and `command_runs` ends the run there. The same
    shape hides `--out$( )put=` and `ext$( )::`. This string is judged beside
    the ordinary one: one reading is what bash does when the substitution
    prints something, the other what it does when it prints nothing, and both
    have to be safe.

    A parameter expansion is deleted only where it is glued into a word:
    `git ${x}push origin +HEAD:main` runs as its `$( )` spelling does, but
    `git push origin $BRANCH` is traffic this repository writes, and deleting a
    whole word would refuse it for naming no destination. A value assembled at
    run time, `F=--output=x; git log $F`, is the residual the module docstring
    states, and this is not that.
    """
    # One model of bash's quoting, shared (`quote_states`).
    states = quote_states(command)
    out, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            out.append(char)
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            out.append(char)
            out.append(command[index + 1])
            index += 2
            continue
        if command.startswith("$(", index):
            close = _closing_paren(command, index + 2)
            if close is None:
                break
            index = close + 1
            continue
        if char == "`":
            close = index + 1
            while close < len(command):
                if command[close] == "\\" and close + 1 < len(command):
                    close += 2
                    continue
                if command[close] == "`":
                    break
                close += 1
            if close >= len(command):
                break
            index = close + 1
            continue
        if char == "$" and command[index + 1:index + 2] not in ("'", '"'):
            end = expansion_end(command, index)
            if end is None and command.startswith("${", index):
                # An unbalanced `${` ends the scan, as `$(` and a backtick do:
                # rescanning from the next `${` is quadratic, and a hook that
                # produces no output within its timeout is non-blocking.
                break
            if end is not None and glued(command, index, end):
                index = end
                continue
        out.append(char)
        index += 1
    return "".join(out) + command[index:] if index < len(command) else "".join(out)


def outside_verbatim(command, reading):
    """`reading` applied to `command` except inside a non-expanding body.

    A quoted heredoc body expands nothing, so rewriting one invents text the
    shell never produces: a body line reading `${x:-EOF}` would become an early
    terminator, and the rest of an innocent filing would be read as commands.

    An expanding body is left to the reading, because bash does expand there.
    """
    spans = [(start, end) for start, end, expands in heredoc_spans(command)
             if not expands]
    if not spans:
        return reading(command)
    out, cursor = [], 0
    for start, end in spans:
        out.append(reading(command[cursor:start]))
        out.append(command[start:end])
        cursor = end
    out.append(reading(command[cursor:]))
    return "".join(out)


def rewriting_expansions(command, replace):
    """`command` with each parameter expansion put through `replace`.

    `replace(text)` is given the expansion as written and returns what to put
    in its place, or None to leave it alone. Single-quoted regions are left
    untouched, because a `$` is literal there.
    """
    # One model of bash's quoting, shared (`quote_states`).
    states = quote_states(command)
    out, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            out.append(char)
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            out.append(char)
            out.append(command[index + 1])
            index += 2
            continue
        if char == "$" and command[index + 1:index + 2] not in ("'", '"'):
            end = expansion_end(command, index)
            if end is None and command.startswith("${", index):
                break
            if end is not None:
                written = replace(command[index:end])
                out.append(command[index:end] if written is None else written)
                index = end
                continue
        out.append(char)
        index += 1
    return "".join(out)


def splitting_expansions(command):
    """`command` with every parameter expansion read as whitespace.

    An expansion can split one word into several: `${IFS}` holds a space by
    default, so `git push${IFS}origin +HEAD:main` is the entire force push
    written as one `shlex` token.

    Read beside the other readings rather than instead of them: an expansion is
    empty, or whitespace, or its own default text, and the command is only safe
    if it is safe under all of them.
    """
    return rewriting_expansions(command, lambda _text: " ")


# `${name:-word}` and its family. The operator decides when the default is
# used; every one of them can put `word` on the command line.
DEFAULTED = re.compile(r"^\$\{[^{}:=?+-]*(?::?[-=?+])(?P<word>.*)\}$", re.DOTALL)


def defaulted_expansions(command):
    """`command` with every `${name:-word}` read as its `word`.

    This is not the run-time residual the module docstring states: the
    dangerous text is literally in the source and an unset variable is the
    default state of the shell, so `git ${x:-push} origin +HEAD:main` is a
    force push written in plain sight.
    """
    def written(text):
        match = DEFAULTED.match(text)
        return None if match is None else match.group("word")

    return rewriting_expansions(command, written)


# A brace expansion that yields exactly one word is pure obfuscation of the
# text inside it, and `{`/`}` are in neither `METACHARACTERS` nor
# `PUNCTUATION`, so `p{u..u}sh` survived as one opaque token.
BRACE = re.compile(r"\{(?P<from>[^{}.,\s]+)(?:\.\.(?P<to>[^{}.,\s]+)|,(?P<rest>[^{}]*))\}")


def brace_expanded(command):
    """`command` with each brace expansion read as its first alternative.

    A single-element range — `p{u..u}sh` — is exactly `push` to bash, and a
    list takes its first word, which is the reading that hides a literal.
    """
    def written(match):
        if match.group("to") is not None:
            return match.group("from") if match.group("to") == match.group("from") else match.group(0)
        return match.group("from")

    return BRACE.sub(written, command)


def dollar_quotes(command):
    """Every `$'…'` and `$"…"` in `command`, as `(start, end, ansi_c)`.

    These are quoting forms and `shlex` has no rule for either, so the `$`
    stays glued outside the quote and the token is `$git` rather than `git`:
    `program_name` matches nothing and every check inside `git_segments`' loop
    is skipped, while bash runs `$'git' push origin +HEAD:main`, `$'g'it …` and
    `git p$'ush' …` as the push.

    `end` is just past the closing quote, and `ansi_c` says which form it is,
    because only `$'…'` decodes escapes.
    """
    # One model of bash's quoting, shared (`quote_states`).
    states = quote_states(command)
    found, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] in ("single", "double"):
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            index += 2
            continue
        if (char == "$"
                and command[index + 1:index + 2] in ("'", '"')):
            # Neither form is a quoting form inside double quotes, which the
            # state test above settles: `"regex $'\\d' matches"` is an ordinary
            # message, and decoding `"$'\\x22'"` into a single-quoted word
            # inside the double quotes would unbalance the line and send it
            # to the `ValueError` path, where the push allow-list does not run.
            #
            # The closer is escape-aware: a plain `find` closes `$"\"'"` on the
            # escaped quote and reads the `'` after it as opening single
            # quotes, hiding a later `$'push'`.
            quote = command[index + 1]
            close = index + 2
            while close < len(command):
                if command[close] == "\\" and close + 1 < len(command):
                    close += 2
                    continue
                if command[close] == quote:
                    break
                close += 1
            if close >= len(command):
                break
            found.append((index, close + 1, quote == "'"))
            index = close + 1
            continue
        index += 1
    return found


ANSI_C_SIMPLE = {
    "a": "\a", "b": "\b", "e": "\x1b", "E": "\x1b", "f": "\f", "n": "\n",
    "r": "\r", "t": "\t", "v": "\v", "\\": "\\", "'": "'", '"': '"', "?": "?",
}


def decode_ansi_c(body):
    """The text `$'<body>'` names, or None where an escape is not decodable.

    Decoding rather than refusing every escape admits ordinary traffic such as
    `grep -n $'\\t' file.txt`, and a list is affordable here because it only
    decides how much honest traffic is admitted, never whether a bypass gets
    through: an escape this does not know returns None and the command is
    refused, so a gap costs a false positive rather than a force push.
    """
    out, index = [], 0
    while index < len(body):
        char = body[index]
        if char != "\\":
            out.append(char)
            index += 1
            continue
        if index + 1 >= len(body):
            return None
        escape = body[index + 1]
        if escape in ANSI_C_SIMPLE:
            out.append(ANSI_C_SIMPLE[escape])
            index += 2
            continue
        if escape in "01234567":
            # `\\0nnn` counts its three digits after the zero — bash reads
            # `$\'\\0165\'` as `u` — and the bare `\\nnn` form keeps its own
            # count.
            first = index + 2 if escape == "0" else index + 1
            digits = body[first:first + 3]
            while digits and not all(d in "01234567" for d in digits):
                digits = digits[:-1]
            out.append(chr(int(digits, 8) & 0xFF) if digits else "\0")
            index += (first - index) + len(digits)
            continue
        if escape in "xuU":
            width = {"x": 2, "u": 4, "U": 8}[escape]
            digits = body[index + 2:index + 2 + width]
            while digits and not all(d in "0123456789abcdefABCDEF" for d in digits):
                digits = digits[:-1]
            if not digits:
                return None
            # `chr` raises above 0x10FFFF, and a hook that raises fails open:
            # exit 1 with empty stdout is a non-blocking error to `PreToolUse`.
            point = int(digits, 16)
            if point > 0x10FFFF:
                return None
            out.append(chr(point))
            index += 2 + len(digits)
            continue
        if escape == "c":
            if index + 2 >= len(body):
                return None
            # `str.upper()` is not length-preserving in Unicode — `ß`
            # upper-cases to `SS` — and `ord` raises on what it returns, which
            # would fail the hook open.
            control = body[index + 2]
            folded = control.upper()
            if len(folded) != 1:
                return None
            out.append(chr(ord(folded) ^ 0x40))
            index += 3
            continue
        return None
    # A NUL truncates the word in bash: `$'a\\0b'` is the single byte `a`, so
    # `git p$'\\0'ush` is `git push`. Truncating models the shell exactly.
    text = "".join(out)
    return text.split("\0", 1)[0]


def single_quoted(text):
    """`text` as a single-quoted shell word, whatever it contains."""
    return "'" + text.replace("'", "'\"'\"'") + "'"


def unreadable_dollar_quote(command):
    """Why `command`'s `$'…'` or `$"…"` cannot be read, or None.

    Two different reasons: a plain `$"safe"` carries no escape at all, and is
    refused because its translation is a lookup in a catalogue this hook is
    not given, so reporting it as an undecodable escape would send a caller
    looking for one.
    """
    for _start, _end, ansi_c in dollar_quotes(command):
        if not ansi_c:
            return (
                "a `$\"…\"` is a translated string, so what the word says is "
                "decided by a message catalogue this guard is not given; "
                "refusing rather than reading the source as if it were the "
                "result."
            )
    if undecodable_dollar_quote(command):
        return (
            "a `$'…'` carries an escape this guard does not decode, so it "
            "cannot tell what the word says; refusing rather than reading "
            "part of it."
        )
    return None


def undecodable_dollar_quote(command):
    """Whether a `$'…'` or `$"…"` in `command` carries an escape to decode.

    The decision `undecodable_heredoc` makes, one construct along, for the
    same reason: decoding every escape bash supports is a list that trails
    bash's. `$'…'` fails when it carries an escape outside the set
    `decode_ansi_c` knows.

    `$"…"` fails unconditionally, because bash resolves it through gettext
    against `TEXTDOMAIN` and `TEXTDOMAINDIR`, both ordinary environment
    variables, so a catalogue placed in the checkout decides what the word
    says — `$"safe"` in command position can run `git`. That is the residual
    `docs/harness-boundaries.md` names for a script on disk, text the shell is
    told rather than given, and the answer is the same: what cannot be read is
    not judged, and what is not judged is refused. Nothing in this repository
    writes `$"…"`.
    """
    for start, end, ansi_c in dollar_quotes(command):
        if not ansi_c:
            return True
        if decode_ansi_c(command[start + 2:end - 1]) is None:
            return True
    return False


def strip_dollar_quotes(command):
    """`command` with every `$'…'` and `$"…"` replaced by what it names.

    `shlex` has no rule for either form, so `$'git'` would tokenise as `$git`,
    which `program_name` does not match (`dollar_quotes`).

    The escapes are decoded rather than dropped, so `$'\\x67it'` becomes `git`
    and is judged as one. A body this file cannot read is refused before this
    runs — which is every locale-quoted one, and an ANSI-C one carrying an
    escape outside the decoded set — so neither the `None` case nor the
    translated form can arrive here.
    """
    out, cursor = [], 0
    for start, end, ansi_c in dollar_quotes(command):
        if not ansi_c:
            continue
        text = decode_ansi_c(command[start + 2:end - 1])
        if text is None:
            continue
        out.append(command[cursor:start])
        out.append(single_quoted(text))
        cursor = end
    out.append(command[cursor:])
    return "".join(out)


def join_continuations(command, quotes=True):
    """`command` with every line continuation removed, as bash removes them.

    A backslash-newline is deleted before the shell tokenises anything, so
    `git 2\\<newline>>&1 push origin +HEAD:main` reaches git as
    `git push origin +HEAD:main` with `2>&1` applied, and
    `git \\<newline>push origin +HEAD:main` is the push too. A continuation is
    neither a separator (`separate_lines` keeps the pair) nor an argument, so
    it is removed here, before anything reads a word, which is the order bash
    uses.

    Inside single quotes a backslash is literal, so a continuation there is two
    ordinary characters and stays. Inside double quotes bash removes it, and so
    does this.

    `quotes` is false for a heredoc body, where a quote is an ordinary
    character and the continuation goes anyway: an expanding body removes
    `\\<newline>` before it expands, so `$\\<newline>(git push …)` in one forms
    a live `$(…)`.
    """
    # One model of bash's quoting, shared (`quote_states`).
    states = quote_states(command, quotes=quotes)
    out, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            out.append(char)
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            if command[index + 1] == "\n":
                index += 2
                continue
            # Any other escape is passed through whole, so an escaped quote
            # never toggles the state below.
            out.append(char)
            out.append(command[index + 1])
            index += 2
            continue
        out.append(char)
        index += 1
    return "".join(out)


def separate_lines(command):
    """`command` with every unquoted newline turned into a `;`.

    A newline separates commands, and `shlex` makes it disappear: with
    `whitespace_split=True` a newline is whitespace, never emitted as a token,
    so every line of a script would join the run before it, and

        echo hi
        git push origin +HEAD:main

    would be one `echo`-led run, exempt under `DATA_ONLY_COMMANDS`.

    A newline inside quotes is data and stays — `git commit -m "a<newline>b"`
    is one argument. So is one after a backslash, which is a line continuation
    bash removes rather than a separator.
    """
    out, escaped = [], None
    for index, in_quotes, in_comment in shell_positions(command):
        char = command[index]
        if index == escaped:
            out.append(char)
            escaped = None
            continue
        if char == "\\" and not in_quotes and not in_comment:
            escaped = index + 1
            out.append(char)
            continue
        if char == "\n" and not in_quotes and not in_comment:
            out.append(";")
        else:
            out.append(char)
    return "".join(out)


# Redirection operators, longest first so that `>>` is never read as a `>`
# with a stray `>` behind it. `<<` and `<<<` are absent from this tuple
# because they are matched before it, each by a branch of its own:
# `redirection_spans` argues both.
REDIRECTION_OPERATORS = ("&>>", "&>", ">>", ">&", ">|", "<>", "<&", ">", "<")


def word_end(command, position, ordinary):
    """The end of the shell word beginning at `position`.

    One parse of a word, shared by the redirection strip and `stdin_scripts`,
    because two parses disagree: ending a here-string at the first unquoted
    metacharacter yields `$` as the script of
    `bash <<<$(printf 'git push origin +HEAD:main')`.

    A substitution is part of the word: the `(` of `$(…)` is not a
    metacharacter to bash but opens a nested command list, and stopping there
    would leave parentheses that `is_boundary` reads as run boundaries,
    severing `git` from its subcommand in
    `git >/tmp/$(echo x) push origin +HEAD:main`. An unbalanced opener stops
    the word instead of swallowing the rest of the line, which would hide
    whatever followed.

    A word may not begin with `(`: `echo <(git push origin +HEAD:main)` is not
    a redirect with `(…)` for a target but a process substitution that runs,
    and consuming it as a word would delete that push from the judged string.
    Left alone, the parentheses stay run boundaries and the inner command is
    judged in its own right.
    """
    def plain(offset):
        return offset < len(command) and ordinary[offset]

    first = position
    while position < len(command):
        char = command[position]
        if not ordinary[position]:
            position += 1
            continue
        if char == "`":
            # Escape-aware, because `\`` is how the legacy form nests, and in
            # agreement with `substitutions`: a plain `find` ends the word at
            # the inner delimiter of `` >/tmp/`echo \`echo x\`` ``.
            scan = position + 1
            while scan < len(command):
                if command[scan] == "\\" and scan + 1 < len(command):
                    scan += 2
                    continue
                if command[scan] == "`":
                    break
                scan += 1
            if scan >= len(command):
                return position
            position = scan + 1
            continue
        if command.startswith("${", position):
            # A parameter expansion is part of the word, metacharacters
            # and all: `>${PATH:+/tmp/x;y}` redirects to `/tmp/x;y`, and
            # returning at that `;` would leave a separator standing between
            # `git` and its subcommand.
            close = _closing_brace(command, position + 2)
            if close is None:
                return position
            position = close + 1
            continue
        if char == "(":
            if position == first:
                return position
            close = _closing_paren(command, position + 1)
            if close is None:
                return position
            position = close + 1
            continue
        if char in METACHARACTERS:
            return position
        position += 1
    return position


def redirection_spans(command):
    """Every redirection in `command`, as `(start, end)` character offsets.

    `start` is the first character of the file descriptor where one is written
    and of the operator otherwise, and `end` is just past the target word — so
    `command[start:end]` is everything bash consumes as redirection syntax and
    never hands to the program.

    A heredoc introducer is one of these, and goes with its delimiter, which
    strands no stray word; left standing, `<<` is whole punctuation and a run
    boundary. A here-string is matched before either, since `<<<` has `<<` as
    a prefix.
    """
    ordinary = ordinary_positions(command)

    def plain(position):
        return position < len(command) and ordinary[position]

    spans, index = [], 0
    while index < len(command):
        if not ordinary[index]:
            index += 1
            continue
        start = index
        digits = index
        while plain(digits) and command[digits].isdigit():
            digits += 1
        if digits == start and command[start] == "{":
            # The descriptor grammar is not only digits: bash takes `{name}`,
            # where name is an identifier, so in `git {fd}>&1 push origin
            # +HEAD:main` a left-behind `{fd}` would stand where the
            # subcommand goes.
            close = start + 1
            if plain(close) and (command[close].isalpha() or command[close] == "_"):
                while plain(close) and (command[close].isalnum()
                                        or command[close] == "_"):
                    close += 1
                if plain(close) and command[close] == "}":
                    digits = close + 1
        begins_word = start == 0 or (
            ordinary[start - 1] and command[start - 1] in METACHARACTERS)
        if digits > start and not begins_word:
            # A descriptor is a whole token glued to the operator, which is
            # bash's own rule rather than an approximation of it: in
            # `echo foo2>x` the word bash writes is `foo2` and only `>x` is
            # syntax. Reading the digits here would be editing an argument,
            # which is the thing `shell_positions` exists to stop this file
            # doing.
            index = digits
            continue
        if command[digits:digits + 3] == "<<<":
            # A here-string's word is data the shell feeds in, exactly like a
            # redirect target — and it is checked before `<<`, which is a
            # prefix of it.
            end = digits + 3
            while plain(end) and command[end] in " \t":
                end += 1
            end = word_end(command, end, ordinary)
            spans.append((start, end))
            index = end
            continue
        if command[digits:digits + 2] == "<<":
            # A heredoc introducer goes with its delimiter: `strip_heredocs`
            # leaves it behind, and `<<` is whole punctuation, so in
            # `git <<EOF push origin +HEAD:main` it would sever `git` from its
            # subcommand. `HEREDOC` is the one parse of that grammar.
            introducer = HEREDOC.match(command, digits)
            if introducer is not None:
                spans.append((start, introducer.end()))
                index = introducer.end()
                continue
            # An introducer this file cannot parse loses only its descriptor.
            if digits > start:
                spans.append((start, digits))
            index = digits + 2
            continue
        operator = None
        for candidate in REDIRECTION_OPERATORS:
            reach = range(digits, digits + len(candidate))
            if command[digits:digits + len(candidate)] == candidate and all(
                    plain(position) for position in reach):
                operator = candidate
                break
        if operator is None:
            index = digits + 1 if digits == start else digits
            continue
        end = digits + len(operator)
        while plain(end) and command[end] in " \t":
            end += 1
        # A process substitution can be the target: left to the run splitter,
        # `(tee /tmp/log)` in `git > >(tee /tmp/log) push origin +HEAD:main`
        # stands as a boundary between `git` and `push`. It is consumed as the
        # word it is, and `substitutions` judges the command inside it.
        if (command[end:end + 2] in (">(", "<(")
                and plain(end) and plain(end + 1)):
            close = _closing_paren(command, end + 2)
            if close is not None:
                spans.append((start, close + 1))
                index = close + 1
                continue
        end = word_end(command, end, ordinary)
        spans.append((start, end))
        index = end
    return spans


def strip_redirections(command):
    """`command` with every redirection removed, target word included.

    A redirection is shell syntax, and to `shlex` the file descriptor in front
    of one is not: `punctuation_chars=True` emits `>&` whole, but the `2` of
    `2>&1` survives as an ordinary word, which reaches every check that counts
    non-flags. In `git push -u origin 2>&1 +HEAD:main` the `2` satisfies
    `SAFE_REF` as the refspec while the real one falls into a run of its own;
    in `git push -u origin feat 2>&1` an honest push names two refspecs.

    Removing the whole redirection makes the remaining string the argv bash
    passes to the program, which is the one thing this hook claims to judge,
    in one strip every path reads.
    """
    out, cursor = [], 0
    for start, end in redirection_spans(command):
        out.append(command[cursor:start])
        cursor = end
    out.append(command[cursor:])
    return "".join(out)


def expandable_regions(command):
    """Every part of `command` the shell would expand, as `(text, quotes)`.

    A quoted heredoc body expands nothing, so a `$(git push …)` in one is
    prose, while an unquoted body expands, and an apostrophe in it is a
    character to bash rather than a quote: `don't $(git push origin +HEAD:main)`
    in one runs the push. `quotes` carries that — inside a heredoc body there
    are no quotes to honour, only expansions to perform.

    The command line itself arrives with bodies and comments already gone, so a
    `$(…)` the shell would never reach cannot be judged as though it would.
    """
    line, regions, cursor = [], [], 0
    for start, end, expands in heredoc_spans(command):
        line.append(command[cursor:start])
        if expands:
            regions.append((command[start:end], False))
        cursor = end
    line.append(command[cursor:])
    return [(strip_comments("".join(line)), True)] + regions


def substitutions(command, quotes=True):
    """Every `$(...)` and backtick body in `command`, innermost included.

    These are commands the shell executes, and `shlex` hands them back as one
    quoted token — so `git log "$(git push origin +HEAD:main)"` contains no
    standalone `git` for the segment scan to find. Extracted and judged in
    their own right.

    `quotes` is false for a heredoc body, where `'` is an ordinary character
    rather than a quote (`expandable_regions`).
    """
    found = []
    index = 0
    # One model of bash's quoting, shared (`quote_states`). `$(` is live
    # inside double quotes, so only the single-quoted state stops it, and the
    # one branch below that needs the double-quoted state reads it from the
    # same list.
    states = quote_states(command, quotes=quotes)
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            # An apostrophe inside double quotes opens nothing, so
            # `git log "don't $(git push origin +HEAD:main)"` still reaches
            # the `$(` below; `quote_states` settles which quotes are live.
            index += 1
            continue
        if char == "\\":
            # `\$(x)` is a literal `$(` to bash, on the command line and in an
            # unquoted heredoc body alike. Skipping the escaped character keeps
            # the guard off a substitution the shell will never perform.
            index += 2
            continue
        if command.startswith("$(", index):
            end = _closing_paren(command, index + 2)
            if end is None:
                break
            found.append(command[index + 2:end])
            index = end + 1
            continue
        if (quotes and states[index] != "double"
                and (command.startswith("<(", index)
                     or command.startswith(">(", index))):
            # A process substitution is a command the shell runs, including
            # one the redirection strip consumes as a target, so it is
            # extracted here. A heredoc body performs no process substitution
            # — parameter, command and arithmetic expansion only — so with
            # `quotes` false, meaning the region is not a command line, a
            # body quoting `<(git push …)` as an example is prose.
            end = _closing_paren(command, index + 2)
            if end is None:
                break
            found.append(command[index + 2:end])
            index = end + 1
            continue
        if command.startswith("${", index) and command[index + 2:index + 3] in (
                " ", "\t", "\n", "|"):
            # Bash 5.3's function substitution runs a command, where every
            # other `${…}` expands a parameter and runs nothing. `${ cmd; }`
            # and `${| cmd; }` are the two spellings, and the character after
            # the brace is what separates them from `${VAR}`. Closed whether
            # or not the host's bash supports it, because an exemption resting
            # on a version expires silently.
            end = _closing_brace(command, index + 2)
            if end is None:
                break
            found.append(command[index + 2:end].lstrip("| \t\n"))
            index = end + 1
            continue
        if char == "`":
            # Escape-aware, because a `\`` is a literal backtick to bash rather
            # than a terminator, and agreeing with the shell about where a
            # substitution ends is the property.
            end = index + 1
            while end < len(command):
                if command[end] == "\\" and end + 1 < len(command):
                    end += 2
                    continue
                if command[end] == "`":
                    break
                end += 1
            if end >= len(command):
                break
            # An escaped backtick is how the legacy form nests:
            # `git log "`echo \`git push origin +HEAD:main\``"` runs the push,
            # so the body is unescaped on the way down for the recursion to see
            # the nested command as a command.
            found.append(command[index + 1:end].replace("\\`", "`"))
            index = end + 1
            continue
        index += 1
    return found


def _closing_brace(command, start):
    """Index of the `}` closing a function substitution, or None.

    The same quoting `_closing_paren` reads, one bracket over, and from the
    same place: `quote_states`. Written as its own function rather than
    parameterised, because the two differ in what nests inside them and a
    shared one would have to be told.

    Over `command[start:]`, not over `command`: a substitution body is
    re-parsed as a fresh command line, so the outer context's quoting does not
    reach inside one — asking about absolute positions would mark the whole
    body of `"$(printf x)"` as double-quoted and lose its own closer.
    """
    states = quote_states(command[start:])
    depth, index = 1, start
    while index < len(command):
        char = command[index]
        if states[index - start] in ("single", "double", "comment"):
            # A `}` inside a quote or a comment closes nothing — a function
            # substitution's body is a command list too.
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            index += 2
            continue
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if not depth:
                return index
        index += 1
    return None


def _closing_paren(command, start):
    """Index of the `)` closing a substitution opened before `start`, or None.

    Quotes are tracked while balancing, through `quote_states`, because a paren
    counter that reads raw characters closes
    `git log "$(printf ')'; git push origin +HEAD:main)"` at the quoted `)`,
    leaving the push hidden in the outer token.

    Over `command[start:]`, not over `command`: a substitution body is
    re-parsed as a fresh command line, so the outer context's quoting does not
    reach inside one — asking about absolute positions would mark the whole
    body of `"$(printf x)"` as double-quoted and lose its own closer.
    """
    states = quote_states(command[start:])
    depth, index = 1, start
    while index < len(command):
        char = command[index]
        if states[index - start] in ("single", "double", "comment"):
            # A substitution's body is a command list, so `#` opens a comment
            # inside it and a `)` in that comment closes nothing, as in
            # `git log "$(echo ok # )` / `git push origin +HEAD:main)"`. A
            # quoted paren closes nothing either.
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            # An unquoted `\)` is a literal paren to bash, so counting it would
            # close the substitution early and hide the rest of it in the outer
            # token: `git log "$(printf \); git push origin +HEAD:main)"`.
            index += 2
            continue
        if command.startswith("$(", index):
            depth += 1
            index += 2
            continue
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if not depth:
                return index
        index += 1
    return None


# A shell invoked with `-c` runs its argument as a command line, and `eval` runs
# the concatenation of its own. Both hand the guard a command it must read as
# one rather than as data.
EVALUATORS = {"bash", "sh", "dash", "zsh", "ksh"}

# `-c`, and the bundles that carry it — `bash -xc <script>` and `bash -cx
# <script>` alike, because `c` need not come last. A long option is never the
# script introducer, so `--` forms are left alone.
SCRIPT_FLAG = re.compile(r"^-[A-Za-z]*c[A-Za-z]*$")


# Windows resolves `git.exe`, `GIT.EXE` and `C:/Git/bin/git.exe` to one
# program, and this repository is developed on Windows.
EXECUTABLE_SUFFIXES = (".exe", ".cmd", ".bat", ".com")


def program_name(token):
    """The program `token` names, normalised for comparison.

    A literal `git` and a `/git` suffix are not the only spellings:
    `git.exe push origin +HEAD:main` and `bash.exe -c` run on Windows.

    Lower-cased because Windows paths are case-insensitive. On a system where
    they are not, `GIT` names nothing and refusing it costs nothing.
    """
    name = re.split(r"[\\/]", token)[-1].lower()
    for suffix in EXECUTABLE_SUFFIXES:
        if name.endswith(suffix):
            return name[: -len(suffix)]
    return name


# `NAME=value` before a command sets a variable for it and is not the command.
# Bash reads `NAME=value`, `NAME+=value`, `NAME[i]=value` and `NAME[i]+=value`
# all as assignment prefixes, so each must be skipped to find the command word
# of `X+=1 printf 'git p%ssh origin +HEAD:main' u | bash`.
ASSIGNMENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(\[[^\]]*\])?\+?=")

# A shell given a helper that reads stdin as a title and a body, in the spelling
# the grants use: nothing here is a quote, an expansion or a `..`, and a
# redirection before the helper is what `bash < a.sh <<EOF` runs the text with.
ISSUE_HELPER_RUN = re.compile(
    r"\s*(?:bash|sh)[ \t]+(?:\./)?\.claude/scripts/"
    r"gh-issue-(?:create|hand|harness|mutation|sweep)\.sh(?=[\s<>]|$)")

# Reserved words the next word of a run is a command word after. `done`, `fi`
# and `}` end a construct and lead nothing.
COMMAND_PREFIX_WORDS = frozenset({"!", "{", "do", "elif", "else", "if", "then",
                                  "until", "while"})


def leading_command(run):
    """The command word of `run`, past any assignment or reserved word.

    `X=1 bash` is a run led by `bash`, and `if echo … | bash; then` by `echo`:
    one function, because the script and printer passes all owe that reading.
    """
    for token in run:
        if not ASSIGNMENT.match(token) and token not in COMMAND_PREFIX_WORDS:
            return token
    return ""


def reads_stdin_as_script(words):
    """Whether a run made of `words` will execute what arrives on its stdin.

    A wrapper in front of a shell — `echo 'git push origin +HEAD:main' |
    command bash`, or `env bash` — still runs the push, so the shell is looked
    for anywhere in the run, and the exemption is `DATA_ONLY_COMMANDS`'
    allow-list rather than a set of wrappers, which fails open on the first
    one nobody listed. Reading any shell name in the run costs an over-refusal
    only in the shape `echo '…git push…' | grep bash`, because the printer
    half of the pipeline pass has to match first.

    A run carrying `-c` reads its script from the argv rather than from stdin,
    and `evaluated_scripts` judges that channel at any position already.
    """
    body = [word for word in words if not ASSIGNMENT.match(word)]
    if not body or program_name(body[0]) in DATA_ONLY_COMMANDS:
        return False
    for position, word in enumerate(body):
        if program_name(word) not in EVALUATORS:
            continue
        # A `-c` before the shell is the wrapper's option: `ionice -c 2 bash`
        # runs bash on its stdin, `-c` there being the scheduling class. Only
        # what follows the shell token can be the shell's script flag.
        if not any(SCRIPT_FLAG.match(element) for element in body[position + 1:]):
            return True
    return False


def _run_words(command, start, end, ordinary):
    """`command[start:end]` split into words on its unquoted metacharacters."""
    words, index = [], start
    while index < end:
        while index < end and command[index] in " 	":
            index += 1
        cursor = index
        while cursor < end and not (
                ordinary[cursor] and command[cursor] in METACHARACTERS):
            cursor += 1
        if cursor > index:
            words.append(command[index:cursor])
        index = cursor if cursor > index else index + 1
    return words


def _run_bounds(command, position, ordinary):
    """The half-open span of the command run containing `position`."""
    start = position
    while start > 0 and not _ends_run(command, start - 1, ordinary):
        start -= 1
    end = position
    while end < len(command) and not _ends_run(command, end, ordinary):
        end += 1
    return start, end


def _ends_run(command, index, ordinary):
    """Whether the character at `index` ends the command run it is in.

    A redirection's `&` does not: bash's lexer is greedy from the left, so it
    is half of `<&`, `>&` or `&>` unless `&` or `|` has claimed it as `&&`, `|&`.
    """
    if not (_unescaped(command, index, ordinary)
            and command[index] in RUN_SEPARATORS):
        return False
    if command[index] != "&":
        return True
    before = _neighbour(command, index, -1, ordinary)
    after = _neighbour(command, index, 1, ordinary)
    if before in ("<", ">"):
        return False
    return not (after == ">" and before not in ("&", "|"))


def _unescaped(command, index, ordinary):
    """Whether `command[index]` is shell syntax: ordinary, and not the
    character an odd run of unquoted backslashes in front of it escapes."""
    slashes = 0
    while (index - slashes > 0 and ordinary[index - slashes - 1]
           and command[index - slashes - 1] == "\\"):
        slashes += 1
    return ordinary[index] and slashes % 2 == 0


def _continues(command, newline):
    """Whether the newline at `newline` is escaped by an odd run of backslashes."""
    slashes = 0
    while newline - slashes > 0 and command[newline - slashes - 1] == "\\":
        slashes += 1
    return slashes % 2 == 1


def _neighbour(command, index, step, ordinary):
    """The syntax character beside `index` in direction `step` (-1 or 1), past
    any line continuations, or "" where that character is quoted or escaped."""
    cursor = index + step
    if step < 0:
        while (cursor > 0 and command[cursor] == "\n"
               and _continues(command, cursor)):
            cursor -= 2
        return (command[cursor]
                if cursor >= 0 and _unescaped(command, cursor, ordinary) else "")
    while (cursor + 1 < len(command) and command[cursor] == "\\"
           and command[cursor + 1] == "\n"):
        cursor += 2
    return command[cursor] if cursor < len(command) and ordinary[cursor] else ""


def forwards_to_evaluator(command, position, ordinary):
    """Whether the run at `position` writes into a shell later in its pipeline.

    A heredoc belongs to the run that opens it and its bytes belong to
    whatever is downstream of the pipe: `cat <<'EOF' | bash` with a push in the
    body runs it, and so does `cat <<<'git push origin +HEAD:main' | bash`,
    while the opener is `cat`'s and `strip_heredocs` removes the only copy of
    the script.

    Only a `|` carries stdout onward, so `||` ends the walk rather than
    continuing it, and a `)` between the run and the pipe is stepped over
    because a subshell writes into the pipe exactly as a bare run does.
    """
    _, index = _run_bounds(command, position, ordinary)
    while index < len(command):
        while index < len(command) and (
                command[index] in " 	"
                or (ordinary[index] and command[index] == ")")):
            index += 1
        if not (index < len(command) and ordinary[index]
                and command[index] == "|") or command.startswith("||", index):
            return False
        index += 2 if command.startswith("|&", index) else 1
        start = index
        while index < len(command) and not _ends_run(command, index, ordinary):
            index += 1
        if reads_stdin_as_script(_run_words(command, start, index, ordinary)):
            return True
    return False


def _helper_reads_stdin_as_data(command, start):
    """Whether the run at `start` is a shell handed an issue helper as its script file.

    A shell handed a file reads stdin as data. Only this literal shape: an
    assignment, a wrapper or a `cd` before it runs the text or moves the path.
    """
    return not command[:start].strip() and ISSUE_HELPER_RUN.match(command, start) is not None


def _consumes_as_script(command, position, ordinary):
    """Whether the script at `position` is executed by its own run or a later
    one in the same pipeline."""
    start, end = _run_bounds(command, position, ordinary)
    return ((reads_stdin_as_script(_run_words(command, start, end, ordinary))
             and not _helper_reads_stdin_as_data(command, start))
            or forwards_to_evaluator(command, position, ordinary))


def pipeline_groups(tokens):
    """`tokens` split into pipelines, each a list of the runs it joins.

    A pipe is not an adjacency: in
    `printf 'git p%ssh origin +HEAD:main' u | cat | bash` neither neighbouring
    pair is a printer feeding a shell, while the shell still runs what the
    printer wrote. `command_runs` drops the boundary between runs; this keeps
    it just long enough to say whether runs share a pipeline.
    """
    groups, current, run = [], [], []
    for token in tokens:
        if not is_boundary(token):
            run.append(token)
            continue
        if run:
            current.append(run)
            run = []
        if token in ("|", "|&"):
            continue
        if current:
            groups.append(current)
            current = []
    if run:
        current.append(run)
    if current:
        groups.append(current)
    return groups


def unmodelled_printer(tokens):
    """Whether a printer whose output this file cannot reproduce feeds a shell.

    Joining a printer's argv is not the bytes it writes, and where the two
    differ the join is the safe-looking one. `printf 'git p%ssh origin
    +HEAD:main' u | bash` runs the push; the join is `git p%ssh origin
    +HEAD:main u`, which every check reads as harmless. `echo -e` does the same
    through its escapes.

    Reproducing `printf` is a specification this file will not carry — the same
    reason it refuses to enumerate git's executing config keys — so the
    unmodellable case refuses instead. The plain forms still go through
    `evaluated_scripts`, which judges the literal text, so `echo 'git status'
    | bash` is unaffected.
    """
    for group in pipeline_groups(tokens):
        shells = [position for position, run in enumerate(group)
                  if reads_stdin_as_script(run)]
        if not shells:
            continue
        for run in group[:max(shells)]:
            # A run can be assignments and nothing else, for which
            # `leading_command` answers `""`, which `list.index` would not
            # find: `X=1 | bash` would crash the hook, and a crash is empty
            # stdout, which `PreToolUse` treats as non-blocking.
            command_word = leading_command(run)
            if not command_word:
                continue
            name = program_name(command_word)
            arguments = run[run.index(command_word) + 1:]
            if name == "printf" and any("%" in element for element in arguments):
                return True
            if name == "echo" and any(element.startswith("-") and "e" in element
                                      for element in arguments):
                return True
    return False


def stdin_scripts(command):
    """Every script a shell in `command` is handed on its stdin.

    `evaluated_scripts` reads the argv element after `-c`; a shell also runs
    what arrives on stdin, and both spellings of that put the script text in
    the command string where a hook can read it:

        bash <<<'git push origin +HEAD:main'
        bash <<EOF
        git push origin +HEAD:main
        EOF

    This is not the residual of a script on disk (`bash script.sh`) or a
    computed one (`sh -c "$(echo …)"`): the script is a literal word in the
    argv, as it is in `bash -c '…'`, which this guard refuses.

    Every other reader of these constructs is left alone, which is what keeps
    `git commit -F - <<EOF` a filing rather than a command: the leading word of
    the run has to be a shell.
    """
    # The bodies first, for `undecodable_heredoc`'s reason: an apostrophe in
    # one would leave this scan in quote state for everything after it.
    openers = []
    spans = heredoc_spans(command, openers=openers)
    ordinary = [False] * len(command)
    literal = [False] * len(command)
    escaped = None
    for index, in_quotes, in_comment in shell_positions(
            command, [(start, end) for start, end, _ in spans]):
        ordinary[index] = not in_quotes and not in_comment
        if index == escaped:
            # An escaped metacharacter is part of the word: treated as a
            # boundary, the here-string of
            # `bash <<<git\\ push\\ origin\\ +HEAD:main` would yield `git\\`
            # alone while the redirection strip removes the whole thing.
            literal[index] = True
            escaped = None
            continue
        if ordinary[index] and command[index] == "\\":
            literal[index] = True
            escaped = index + 1

    def boundary(position):
        return (ordinary[position] and not literal[position]
                and command[position] in METACHARACTERS)

    # Bodies belong to introducers in order: in `bash <<A; cat <<B` the first
    # body is `bash`'s, though the last introducer before it is `<<B`.
    # `heredoc_spans` names each span's own opener, so a `<<` it did not read as
    # one, a shift or an undecodable delimiter, holds no place here.
    for (start, end, _expands), opener in zip(spans, openers):
        if _consumes_as_script(command, opener, ordinary):
            yield command[start:end]

    index = 0
    while index < len(command):
        if not (ordinary[index] and command.startswith("<<<", index)):
            index += 1
            continue
        consumed = _consumes_as_script(command, index, ordinary)
        cursor = index + 3
        while cursor < len(command) and command[cursor] in " \t":
            cursor += 1
        word = word_end(command, cursor, [not literal[position] and value
                                          for position, value
                                          in enumerate(ordinary)])
        if consumed:
            # A here-string is quote-removed before the shell runs it, and
            # `shlex` has no rule for either dollar quote, so
            # `bash <<<$'git push origin +HEAD:main'` would reach the recursion
            # as `$git push …`, a name `program_name` does not match.
            #
            # An undecodable one is yielded whole rather than normalised: the
            # recursive judge applies the same fail-closed check to it and
            # refuses with the reason that check states.
            text = command[cursor:word]
            if undecodable_dollar_quote(text):
                yield text
                index = max(word, index + 3)
                continue
            try:
                parts = shlex.split(strip_dollar_quotes(text), posix=True)
            except ValueError:
                parts = [text]
            if parts:
                yield " ".join(parts)
        index = max(word, index + 3)


def substitution_fed_shells(command):
    """Whether a process substitution supplies a shell in `command` its script.

    `bash < <(printf '%s\\n' 'git push origin +HEAD:main')` runs the push,
    while each pass judges a half: the inner `printf` is data, the redirection
    strip removes `< <(…)` whole as the redirect target, and what is left is a
    `bash` with no script. `bash <(echo …)` runs it too, the substitution being
    a filename the shell is told to execute.

    Refused rather than read, on `unmodelled_printer`'s argument: what the
    shell executes is the substitution's output, and reading the inner command
    instead would be right for `<(echo '…')` and fail open for every spelling
    that computes.

    A run led by a printer is left alone, exactly as the pipeline pass leaves
    one: `echo <(git push origin +HEAD:main)` is text, and the inner command is
    judged in its own right by `substitutions`.
    """
    ordinary = [False] * len(command)
    escaped = None
    bodies = [(start, end) for start, end, _ in heredoc_spans(command)]
    for index, in_quotes, in_comment in shell_positions(command, bodies):
        if index == escaped:
            escaped = None
            continue
        if command[index] == "\\" and not in_quotes and not in_comment:
            escaped = index + 1
            continue
        ordinary[index] = not in_quotes and not in_comment

    for index in range(len(command) - 1):
        if not (ordinary[index] and ordinary[index + 1]):
            continue
        if command[index:index + 2] not in ("<(", ">("):
            continue
        start, end = _run_bounds(command, index, ordinary)
        if reads_stdin_as_script(_run_words(command, start, end, ordinary)):
            return True
    return False


def evaluated_scripts(tokens):
    """Every token a shell evaluator in `tokens` will execute as a command.

    `shlex` hands a quoted script back as one data token, as it does a command
    substitution — so `git log "$(bash -c 'git push origin +HEAD:main')"`
    would reach the inner pass as `bash`, `-c` and one opaque string, with no
    `git` for the segment scan to find.

    The data-only boundary applies here too: a run led by `echo` is text, so
    `echo bash -c \'git push …\'` quotes a command rather than running one.

    The bound is a script this hook can read. `bash script.sh` runs a file,
    and a hook is handed an argv rather than a filesystem, the same shape as
    the parameter-expansion residual. A here-string or heredoc script is in
    the command string, and `stdin_scripts` reads it.
    """
    for run in command_runs(tokens):
        if not run or program_name(run[0]) in DATA_ONLY_COMMANDS:
            continue
        for index, token in enumerate(run):
            name = program_name(token)
            if name in EVALUATORS:
                argv = run[index + 1:]
                for position, element in enumerate(argv):
                    if SCRIPT_FLAG.match(element) and position + 1 < len(argv):
                        yield argv[position + 1]
                        break
            elif name == "eval":
                argv = run[index + 1:]
                if argv:
                    yield " ".join(argv)

    # A shell with no script of its own reads one from the pipe, and the run
    # before it is where that text is written: `echo 'git push origin
    # +HEAD:main' | bash` runs the push. A printer's arguments are text under
    # `DATA_ONLY_COMMANDS` until a shell is on the other end of the pipe.
    for group in pipeline_groups(tokens):
        shells = [position for position, run in enumerate(group)
                  if reads_stdin_as_script(run)]
        if not shells:
            continue
        for before in group[:max(shells)]:
            command_word = leading_command(before)
            if program_name(command_word) not in DATA_ONLY_COMMANDS:
                continue
            # Sliced past the command word rather than past the first token:
            # with an assignment prefix the two differ, and `before[1:]` would
            # hand the judgement a string beginning `echo`, which the
            # data-only exemption waves through.
            spoken = before[before.index(command_word) + 1:]
            written = [element for element in spoken
                       if not element.startswith("-")]
            if written:
                yield " ".join(written)


# Commands whose arguments are text and never a command line.
#
# An allow-list, and the direction is load-bearing. A name missing from
# here costs an over-refusal; the converse — listing the wrappers that do
# execute their arguments — fails open on the first one nobody thought of, and
# `timeout`, `env`, `nohup`, `xargs`, `sudo`, `command` and `time` all run
# `git push origin +HEAD:main` perfectly well. A run led by anything not named
# here keeps reaching the scan.
DATA_ONLY_COMMANDS = {"echo", "printf", ":", "true", "false"}

# A run led by one of these reads or lists the words after it, and cannot run
# them: `grep -n tee .claude/x` writes nothing, and `git rm` is `git_path_offence`'s.
READING_COMMANDS = frozenset({
    "cat", "diff", "file", "git", "grep", "head", "ls", "stat", "tail", "wc",
})


# `shlex(punctuation_chars=True)` emits a maximal run of these as one token, so
# an operator can arrive glued to its neighbour and match no separator by name.
PUNCTUATION = set("();<>|&")

# What bash treats as a word separator when unquoted. Not the same set as
# PUNCTUATION, which is `shlex`'s: this one carries the whitespace, because
# the question it answers is where a word begins rather than where a token
# does.
METACHARACTERS = set("|&;()<> \t\n")

# What ends a command run. A subset of METACHARACTERS: a redirection
# operator and a space separate words within one run rather than ending
# it.
RUN_SEPARATORS = set(";&|()\n")


def is_boundary(token):
    """Whether `token` ends the command run it appears in.

    A token made entirely of shell punctuation is a boundary whatever it is
    glued into: `);` is one token, and in
    `git log -1; (echo ok);git push origin +HEAD:main` the push must not stay in
    a run led by `echo`. That also settles `<(`: a process substitution is
    executed before the command it is an argument to, so the `git` inside
    `echo <(git push origin +HEAD:main)` belongs to no printer's run.
    """
    return token in SEPARATORS or (
        token != "" and all(char in PUNCTUATION for char in token))


def command_runs(tokens):
    """`tokens` split into the separate commands the shell would run."""
    current = []
    for token in tokens:
        if is_boundary(token):
            if current:
                yield current
            current = []
        else:
            current.append(token)
    if current:
        yield current


def git_segments(tokens):
    """Yield the argv slice of every `git` invocation in a compound command.

    A `git` token is only an invocation where a command can stand:
    `echo git push origin +HEAD:main` is text, and a guard that refuses honest
    traffic gets turned off.

    The test is the run's leading word, not where `git` sits inside it, because
    a wrapper puts the real command in the middle — which is why the scan still
    covers the whole run.
    """
    for run in command_runs(tokens):
        if not run or program_name(run[0]) in DATA_ONLY_COMMANDS:
            continue
        for index, token in enumerate(run):
            if program_name(token) != "git":
                continue
            yield run[index + 1:]


def after_global_options(segment):
    """`segment` from its subcommand onward, with git's global options dropped."""
    index = 0
    while index < len(segment) and segment[index].startswith("-"):
        index += 2 if segment[index] in GLOBAL_VALUE_FLAGS else 1
    return segment[index:]


def global_options(segment):
    """`segment`'s leading global options — everything before the subcommand.

    The position is the whole point: `-c` before the subcommand is git's
    configuration option, and `-c` after `commit` is "reuse this commit's
    message". Refusing the second would break an ordinary commit, so the two
    are told apart the way git tells them apart — by where they stand.
    """
    stripped = after_global_options(segment)
    if not stripped:
        return segment
    return segment[:len(segment) - len(stripped)]


def subcommand_of(segment):
    stripped = after_global_options(segment)
    return stripped[0] if stripped else ""


def push_offence(segment):
    """The reason to refuse a `git push`, or None — by allow-list.

    `push` is located rather than assumed to be first, so no global option,
    known or not, can hide it.
    """
    # `push` is the subcommand when everything before it is either an option or
    # an option's value — and a value is recognised structurally, as a non-flag
    # immediately preceded by a flag, rather than by consulting a list of
    # value-taking globals. That is what makes an unknown global harmless:
    # `git --attr-source HEAD push` and `git --some-future-global X push` both
    # resolve, without this file knowing either flag.
    #
    # It also keeps `git log push` — a ref that happens to be called `push` —
    # out of the push checks, because `log` is a non-flag that no flag precedes,
    # so `log` is the subcommand and `push` is one of its arguments: a guard
    # that fires on innocent traffic is one somebody turns off.
    start = None
    for index, element in enumerate(segment):
        if element.startswith("-"):
            continue
        if element == "push":
            start = index
            break
        if index == 0 or not segment[index - 1].startswith("-"):
            break  # this is the subcommand, and it is not `push`
    if start is None:
        return None
    rest = segment[start + 1:]

    for element in rest:
        if element.startswith("-") and element not in PUSH_ALLOWED_FLAGS:
            return (
                f"`git push {element}` is not one of the options this guard "
                "recognises. A push is admitted only when every part of it is "
                "known — one remote, one refspec naming a destination, and "
                "options from a fixed set. Refusing what is unrecognised is "
                "what stops the next spelling nobody listed."
            )

    positional = [a for a in rest if not a.startswith("-")]
    if len(positional) != 2:
        return (
            "a push must name a remote and exactly one refspec. "
            "`git push origin` and `git push origin HEAD` name no destination, "
            "so neither can be shown not to be a protected branch — a hook is "
            "given no repository state to resolve them against."
        )

    remote, refspec = positional
    if not SAFE_REMOTE.match(remote):
        return f"`{remote}` is not a plain remote name"

    if refspec.startswith("+"):
        return "a `+` refspec is a force push — the spelling that carries no `--force`"
    if ":" in refspec:
        source, _, destination = refspec.partition(":")
        if not source:
            return "a `:branch` refspec deletes the remote branch"
    else:
        source, destination = refspec, refspec
    # One prefix, not a loop: `refs/heads/heads/main` is a branch named
    # `heads/main`, and `heads/refs/heads/main` another, neither of them main.
    for prefix in ("refs/heads/", "heads/"):
        if destination.startswith(prefix):
            destination = destination[len(prefix):]
            break
    if source in UNRESOLVABLE_SOURCES and destination == source:
        return (
            f"`{source}` names no destination of its own; it updates whatever "
            "branch you are standing on, which a hook cannot resolve"
        )
    if not SAFE_REF.match(destination):
        return (
            f"`{destination}` is not a plain branch name. A wildcard or pattern "
            "destination can include a protected branch while equalling none — "
            "`refs/heads/*:refs/heads/*` is the case that made this an "
            "allow-list."
        )
    if destination in PROTECTED_BRANCHES:
        return (
            f"pushing to `{destination}` is a decision, not a step, in every "
            "spelling of the refspec"
        )
    return None


# The checks below refuse a trap rather than an attack: the command does what it
# says, and what it says loses work. Each message names the form that works, so
# the remedy arrives at the refusal (`docs/harness-boundaries.md`, *Git argv
# guard hook*).
UNCHECKED_PUSH = (
    "`git push` here pipes its output into another command and the pipeline "
    "is chained with `&&` or `||`, so the chain reads the last command's exit "
    "status, not the push's: a push that failed runs what follows as though it "
    "landed. Put `set -o pipefail;` before the pipeline, or push unpiped and "
    "compare `git ls-remote origin refs/heads/<branch>` with `git rev-parse "
    "HEAD` before acting on it."
)
LITERAL_CACHEINFO = (
    "`git update-index --cacheinfo` with a literal mode stages that mode "
    "whatever the file had, so a hard-coded one drops or adds an executable "
    "bit, and a Windows checkout never shows it in `git status`. Read the mode with "
    "`git ls-files -s <path>` and pass it through, then expect no `mode change` "
    "line in `git diff --summary origin/main...HEAD`."
)
REMOTE_RESET = (
    "`git reset --soft` to a remote-tracking ref names a ref every worktree "
    "shares and any fetch moves: a fetch between the fork and the reset puts "
    "the branch on the new base with the old tree, and the recommit reverts "
    "whatever landed meanwhile. Reset to the fork point by its SHA — "
    "`git merge-base HEAD origin/main`, read before any fetch — and check "
    "`git diff --stat origin/main...HEAD` before pushing."
)
STDIN_DATA_ROUTE = (
    " If the shell runs a script file and this text is that script's input, "
    "write the text to a file with `Write` and pass it as `< <path>`: this "
    "guard reads a shell's stdin as a script, quoted delimiter or not, since "
    "it cannot see what the script does with it. An issue helper is read as "
    "taking data only as the first thing the command does, spelled "
    "`bash .claude/scripts/gh-issue-sweep.sh <args> <<'DELIM'`, which is the "
    "route where `Write` is denied, as in a sweep; "
    "`docs/harness/pitfalls.md`, *Shell and encoding*, lists the shapes "
    "another check refuses there too."
)
CACHEINFO_MODE = re.compile(r"^[0-7]{6}(?:,|$)")
REMOTE_TRACKING = re.compile(r"^(?:refs/remotes/)?origin/")

# A subset by hand of the keys a written value runs a program from, `git -c`
# outliving the command. `core.editor`, `credential.helper` and the rest stay
# admitted, with a read and a removal, or `rerere.enabled` is refused with them.
RUNNING_CONFIG_KEY = re.compile(
    r"(?i)^(?:alias\..+|core\.pager|core\.hookspath|core\.sshcommand|diff\.external|.+\.cmd)$")
CONFIG_VALUE_OPTIONS = frozenset({"-f", "--file", "--blob", "--type", "--default",
                                  "--comment", "--value"})
CONFIG_WRITE_OPTIONS = frozenset({"--add", "--replace-all"})
CONFIG_OTHER_OPTIONS = frozenset({
    "--edit", "-e", "--get", "--get-all", "--get-color", "--get-colorbool",
    "--get-regexp", "--get-urlmatch", "--list", "-l", "--remove-section",
    "--rename-section", "--unset", "--unset-all"})
CONFIG_OTHER_ACTIONS = frozenset({"edit", "get", "list", "remove-section",
                                  "rename-section", "unset"})


def _is_push(segment):
    """Whether `segment` is a `git push`, located as `push_offence` locates it."""
    for index, element in enumerate(segment):
        if element.startswith("-"):
            continue
        if element == "push":
            return True
        if index == 0 or not segment[index - 1].startswith("-"):
            return False
    return False


def _sets_pipefail(words):
    """Whether a `set` run's arguments turn `pipefail` on: `-o pipefail`, or a cluster such as `-euo pipefail`."""
    return any(word.startswith("-") and "o" in word[1:] and following == "pipefail"
               for word, following in zip(words, words[1:]))


def unchecked_push_pipeline(tokens):
    """Whether a `git push` is piped and its pipeline chained by `&&` or `||` with no `set -o pipefail` first."""
    runs, current = [], []
    for token in list(tokens) + [";"]:
        if is_boundary(token):
            runs.append((current, token))
            current = []
        else:
            current.append(token)
    pipefail = piped_push = False
    for run, separator in runs:
        if run and program_name(run[0]) == "set":
            pipefail = pipefail or _sets_pipefail(run[1:])
        elif run and program_name(run[0]) not in DATA_ONLY_COMMANDS and any(
                program_name(token) == "git" and _is_push(run[index + 1:])
                for index, token in enumerate(run)):
            piped_push = not pipefail and separator == "|"
            continue
        if piped_push and separator in ("&&", "||"):
            return True
        if separator != "|":
            piped_push = False
    return False


def short_flag(element, subcommand, letters):
    """Which of `letters` the short option word `element` carries, or `None`."""
    if not element.startswith("-") or element.startswith("--"):
        return None
    stops = GLUED_VALUE_LETTERS.get(subcommand, "")
    for letter in element[1:]:
        if letter in letters:
            return letter
        if letter in stops:
            return None
    return None


def xargs_supplies_push(tokens):
    """Whether `xargs` hands a `git push` arguments this guard never sees.

    Only the push, whose destination cannot be read: `xargs` before anything
    else is ordinary, and a run led by a reader names it as data.
    """
    for run in command_runs(tokens):
        if program_name(leading_command(run)) in (
                READING_COMMANDS | DATA_ONLY_COMMANDS):
            continue
        names = [program_name(token) for token in run]
        if "xargs" not in names:
            continue
        for index in range(names.index("xargs") + 1, len(run)):
            if names[index] == "git" and subcommand_of(run[index + 1:]) == "push":
                return True
    return False


def fetch_offence(segment):
    """The reason to refuse a fetch or pull that can move a branch backwards.

    `-f`, and a fetch's `-u`, are what discard local commits; `refspec_offence`
    judges a `+` in a refspec, as it judges every destination.
    """
    stripped = after_global_options(segment)
    if not stripped or stripped[0] not in ("fetch", "pull"):
        return None
    kind = stripped[0]
    for element in stripped[1:]:
        name = element.split("=", 1)[0]
        if (short_flag(element, kind, "fu" if kind == "fetch" else "f")
                or (len(name) > 3 and "--force".startswith(name))
                or (kind == "fetch" and len(name) > 9
                    and "--update-head-ok".startswith(name))):
            return (
                f"`git {kind} {element}` can move a local branch to a commit "
                "that does not contain it, which discards local commits under "
                "a read's grant. Fetch without it; `main:main` fast-forwards."
            )
    return None


# A fetch's or a pull's options that take the next word as their value, so a
# colon in it is no refspec's. One this misses reads as a refspec, which costs a
# false refusal at worst.
FETCH_VALUE_OPTIONS = frozenset({
    "--deepen", "--depth", "--filter", "--jobs", "--negotiation-tip",
    "--server-option", "--shallow-exclude", "--shallow-since", "--strategy",
    "--strategy-option", "-X", "-j", "-o", "-s",
})

# A branch of origin onto its own tracking ref, or a tag onto itself: only the
# value origin holds there, so it can poison nothing. The branch is named in full
# because a short name takes origin's same-named tag first. A dependabot rebase
# types that form with `$B` for both names.
ORIGIN_OWN_REF = re.compile(
    r"^\+?refs/(?:heads/(?P<branch>[^:]+):refs/remotes/origin/(?P=branch)"
    r"|tags/(?P<tag>[^:]+):refs/tags/(?P=tag))$")


def refspec_offence(segment):
    """The reason to refuse a fetch or a pull writing a ref of its own choosing.

    A destination, `--refmap` or `--stdin` writes what a sweep and `/ship`'s
    review bar trust, under a read's grant; only `main:main` and `ORIGIN_OWN_REF`
    stay, from origin."""
    stripped = after_global_options(segment)
    if not stripped or stripped[0] not in ("fetch", "pull"):
        return None
    operands, position = [], 1
    while position < len(stripped):
        word = stripped[position]
        name = word.split("=", 1)[0]
        position += 1
        if word in FETCH_VALUE_OPTIONS:
            position += 1
        elif not word.startswith("-"):
            operands.append(word)
        elif ((len(name) > 4 and "--refmap".startswith(name))
              or (stripped[0] == "fetch" and len(name) > 3
                  and "--stdin".startswith(name))):
            return (
                f"`git {stripped[0]} {word}` writes a ref no refspec operand "
                "names, under a read's grant. Fetch without it."
            )
    for refspec in operands[1:]:
        destination = refspec.partition(":")[2]
        # A reading of `$B` as empty or as whitespace leaves one ending in a
        # slash, which git refuses as invalid; the raw reading judges the rest.
        if destination and not destination.endswith("/") and not (
                operands[0] == "origin" and (
                    refspec == "main:main" or ORIGIN_OWN_REF.match(refspec))):
            return (
                f"`{refspec}` writes a ref a plain fetch would not, which a "
                "sweep or a review may then trust. Fetch without a destination "
                "and read FETCH_HEAD; `main:main` from origin fast-forwards, and "
                "`refs/heads/<name>:refs/remotes/origin/<name>` tracks."
            )
    return None


def pull_offence(segment):
    """The reason to refuse an option that overrides `--ff-only`, or `None`."""
    stripped = after_global_options(segment)
    if not stripped or stripped[0] != "pull" or "--ff-only" not in stripped:
        return None
    for element in stripped[1:]:
        name = element.split("=", 1)[0]
        if (name in ("--no-ff", "--ff") or short_flag(element, "pull", "r")
                or (len(name) > 4 and "--rebase".startswith(name))):
            return (
                f"`{element}` beside `--ff-only` overrides it, and the pull "
                "that is granted is the one that cannot rewrite or merge. "
                "Drop the option."
            )
    return None


def config_offence(segment):
    """The refusal for a `git config` writing a key that runs a program, or None."""
    stripped = after_global_options(segment)
    if not stripped or stripped[0] != "config":
        return None
    operands, options, position = [], set(), 1
    while position < len(stripped):
        word = stripped[position]
        position += 1
        if not word.startswith("-"):
            operands.append(word)
            continue
        options.add(word.split("=", 1)[0])
        if word in CONFIG_VALUE_OPTIONS:
            position += 1
    if options & CONFIG_OTHER_OPTIONS or (operands and operands[0] in CONFIG_OTHER_ACTIONS):
        return None
    if operands[:1] == ["set"]:
        operands = operands[1:] + [""]
    if len(operands) < 2 and not options & CONFIG_WRITE_OPTIONS:
        return None
    if not operands or not RUNNING_CONFIG_KEY.match(operands[0]):
        return None
    return (
        f"`git config {operands[0]}` writes a key git runs a program from, the "
        "`git -c` this guard refuses, in a file that outlives this command. "
        "Hand it to the owner if it is wanted; other keys stay admitted."
    )


def trap_offence(segment):
    """The refusal for a git command whose text shows it losing work, or None."""
    stripped = after_global_options(segment)
    if not stripped:
        return None
    subcommand, arguments = stripped[0], stripped[1:]
    # `--add` stages a path the index lacks, which has no mode to read back.
    if subcommand == "update-index" and "--add" not in arguments:
        for index, element in enumerate(arguments):
            value = None
            if element == "--cacheinfo" and index + 1 < len(arguments):
                value = arguments[index + 1]
            elif element.startswith("--cacheinfo="):
                value = element.split("=", 1)[1]
            if value is not None and CACHEINFO_MODE.match(value):
                return LITERAL_CACHEINFO
    if subcommand == "reset" and "--soft" in arguments and any(
            REMOTE_TRACKING.match(element) for element in arguments if not element.startswith("-")):
        return REMOTE_RESET
    return None


# ---- what a command writes -------------------------------------------------

# The directory the command runs in, from the hook event; `main` sets it.
EVENT_CWD = None

WRITING_REDIRECTION = re.compile(
    r"^(?:\d+|\{\w+\})?(?P<operator>&>>|&>|>>|>&|>\||<>|>)[ \t]*(?P<target>.*)$",
    re.DOTALL)

# Variables whose value this process shares with the session.
EXPANDABLE_VARIABLES = frozenset({
    "CLAUDE_PROJECT_DIR", "HOME", "TEMP", "TMP", "TMPDIR",
})
VARIABLE_REFERENCE = re.compile(
    r"\$(?:\{([A-Za-z_][A-Za-z0-9_]*)\}|([A-Za-z_][A-Za-z0-9_]*))")

# A worktree removed or not yet forked has no `.git`, and is still one.
WORKTREE_BELOW = re.compile(r"(?i)^\.claude[\\/]worktrees[\\/][^\\/]+[\\/](.*)$")

# Bash on Windows spells `C:\x` as `/c/x`, which `abspath` reads as `C:\c\x`.
DRIVE_SPELLING = re.compile(r"^/([A-Za-z])(?=/|$)")

# What each verb writes: `destination` the last operand or a `-t` value, `every`
# each operand, `in-place` the files under `-i`, and `of` the `of=` operand.
# The list trails the programs that write, which the boundaries file states.
WRITING_VERBS = {
    "cp": "destination", "install": "destination", "ln": "destination",
    "mv": "every", "rm": "every", "rmdir": "every", "shred": "every",
    "tee": "every", "touch": "every", "truncate": "every", "unlink": "every",
    "perl": "in-place", "sed": "in-place",
    "dd": "of",
}
TARGET_OPTION_VERBS = frozenset({"cp", "install", "ln", "mv"})
# Verbs that remove, move or fill a directory whole, so its parent is a write.
TREE_VERBS = frozenset({"cp", "install", "mv", "rm", "rmdir", "shred"})
# Verbs whose operand goes away, so a parent of the checkout goes with it.
REMOVING_VERBS = frozenset({"mv", "rm", "rmdir", "shred", "unlink"})

# Options that take a value, so their value is not read as an operand.
SHORT_VALUE_OPTIONS = {
    "cp": "S", "install": "gmoS", "ln": "S", "mv": "S", "sed": "l",
    "shred": "ns", "touch": "dr", "truncate": "rs",
}
LONG_VALUE_OPTIONS = {
    "cp": ("--suffix",), "install": ("--group", "--mode", "--owner",
                                     "--strip-program", "--suffix"),
    "ln": ("--suffix",), "mv": ("--suffix",),
    "sed": ("--expression", "--file", "--line-length"),
    "shred": ("--iterations", "--random-source", "--size"),
    "touch": ("--date", "--reference", "--time"),
    "truncate": ("--reference", "--size"),
}
FIND_EXECUTORS = frozenset({"-exec", "-execdir", "-ok", "-okdir"})
FIND_OUTPUT_FILES = frozenset({"-fls", "-fprint", "-fprint0", "-fprintf"})
FIND_VALUE_OPTIONS = frozenset({"-D"})
DIRECTORY_CHANGES = frozenset({"cd", "pushd", "popd"})
DIRECTORY_WRAPPERS = frozenset({"builtin", "command"})


def target_literal(word):
    """The text shell `word` spells, or `None` for a substitution or a parse failure."""
    if word.startswith(("<(", ">(")) or substitutions(word):
        return None
    try:
        parsed = shlex.split(strip_dollar_quotes(word), posix=True)
    except ValueError:
        return None
    return parsed[0] if parsed else None


def expanded_target(literal, command):
    """`literal` with its known variables replaced, or `None` if one is unknown.

    Known means this process holds it and `command` does not set it.
    """
    assigned = VARIABLE_REFERENCE.sub("", command)
    failed = False

    def replace(match):
        nonlocal failed
        name = match.group(1) or match.group(2)
        value = os.environ.get(name)
        if (name not in EXPANDABLE_VARIABLES or not value or re.search(
                rf"(?<![A-Za-z0-9_]){name}(?![A-Za-z0-9_])", assigned)):
            failed = True
            return ""
        return value

    expanded = VARIABLE_REFERENCE.sub(replace, literal)
    return None if failed or "$" in expanded else expanded


def written_path(word, cwd, command):
    """The absolute path shell `word` names from `cwd`, or `None` if unreadable.

    `None` admits: refusing an unknown variable or a computed word is the false
    refusal the threat model files.
    """
    literal = target_literal(word)
    if not literal:
        return None
    raw = word.strip()
    if raw.startswith("~"):
        prefix = re.match(r"~[^/\\]*", raw).group(0)
        if prefix == "~":
            home = os.environ.get("HOME") or os.path.expanduser("~")
            literal = home + literal[1:]
        elif prefix == "~+" and cwd:
            literal = cwd + literal[2:]
        else:
            return None
    if "$" in literal:
        literal = expanded_target(literal, command)
        if literal is None:
            return None
    if os.name == "nt":
        # `abspath("C:")` is that drive's working directory, not the drive.
        literal = DRIVE_SPELLING.sub(lambda match: match.group(1) + ":" + (
            os.sep if match.end() == len(literal) else ""), literal)
    if not (os.path.isabs(literal) or literal.startswith(("/", "\\"))):
        if cwd is None:
            return None
        literal = os.path.join(cwd, literal)
    return os.path.normpath(os.path.abspath(literal))


def checkout_of(path):
    """The root of the checkout holding `path`, or `None` outside one."""
    root = path
    while not os.path.exists(os.path.join(root, ".git")):
        parent = os.path.dirname(root)
        if parent == root:
            return None
        root = parent
    return root


def checkout_relative(path):
    """`path` from the root of the checkout holding it, or `None` outside one."""
    root = checkout_of(path)
    if root is None:
        return None
    relative = os.path.relpath(path, root)
    worktree = WORKTREE_BELOW.match(relative)
    return worktree.group(1) if worktree else relative


def in_linked_worktree(path):
    """Whether `path` is in a linked worktree, removed from disk or not."""
    root = checkout_of(path)
    return root is not None and (
        os.path.isfile(os.path.join(root, ".git"))
        or WORKTREE_BELOW.match(os.path.relpath(path, root) + os.sep) is not None)


def beneath(path, root):
    """Whether resolved `path` is `root` or sits under it."""
    path, root = os.path.normcase(path), os.path.normcase(root)
    return path == root or path.startswith(root.rstrip(os.sep) + os.sep)


def scratch_checkout(path):
    """Whether `path` is in a checkout under the temp root the session is not in.

    A mutation proof copies into one, and this target's denies are not about it.
    """
    if not beneath(path, os.path.realpath(tempfile.gettempdir())):
        return False
    session = checkout_of(os.path.abspath(EVENT_CWD or os.getcwd()))
    return session is None or not beneath(path, os.path.realpath(session))


def protected_target(path, whole=False):
    """The deny holding `path`, as spelled or where its links land, or `None`."""
    if scratch_checkout(os.path.realpath(path)):
        return None
    for place in (path, os.path.realpath(path)):
        relative = checkout_relative(place)
        named = denied_path(relative, whole) if relative is not None else None
        if named is not None:
            return named
    return None


def destination_offence(word, cwd, command, writer, whole=False, inside=None):
    """The reason to refuse writing the file shell `word` names, or `None`.

    `inside` is the name a copy gives its source in the directory `word` names.
    """
    path = written_path(word, cwd, command)
    if path is not None and inside is not None:
        path = os.path.join(path, inside)
    named = protected_target(path, whole) if path is not None else None
    if named is None:
        return None
    return (
        f"{writer} would write `{path}`, which {named} protects, and a deny "
        "binds the editing tools alone, so this writes straight past it. Use "
        "an editing tool, where the rule judges the path, or hand it to the "
        "owner."
    )


def removal_offence(word, cwd, command, writer):
    """The reason to refuse removing the place shell `word` names, or `None`.

    The filesystem's root, the home directory and a strict parent of the checkout
    the command starts in or has moved to; `rm -rf .` is the checkout itself.
    """
    path = written_path(word, cwd, command)
    if path is None:
        return None
    # `rm -rf ../*` takes the checkout with the rest of what the star holds.
    if os.path.basename(path) == "*":
        path = os.path.dirname(path)
    place = None
    if os.path.dirname(path) == path:
        place = "the filesystem's root"
    elif os.path.normcase(path) == os.path.normcase(written_path("~", cwd, command) or ""):
        place = "the home directory"
    for start in (EVENT_CWD or os.getcwd(), cwd):
        root = checkout_of(os.path.abspath(start)) if start else None
        if place is None and root is not None and beneath(root, path) and (
                os.path.normcase(root) != os.path.normcase(path)):
            place = f"a parent of the checkout `{root}`"
    if place is None:
        return None
    return (
        f"{writer} would remove `{path}`, {place}, and this checkout or every "
        "session's home with it. Name what is below it."
    )


def ordinary_positions(command):
    """Whether each character of `command` is unquoted, unescaped shell text."""
    ordinary = [False] * len(command)
    escaped = None
    for index, in_quotes, in_comment in shell_positions(command):
        if index == escaped:
            escaped = None
            continue
        if command[index] == "\\" and not in_quotes and not in_comment:
            escaped = index + 1
            continue
        ordinary[index] = not in_quotes and not in_comment
    return ordinary


def raw_runs(command):
    """`command`'s runs in order, as `(words, redirections)`, quoting intact.

    `shlex` removes the quotes that decide whether `~` or `$` expands. A
    subshell's `(` and `)` stand as themselves: a `cd` inside one ends with it.
    """
    ordinary = ordinary_positions(command)
    spans = dict(redirection_spans(command))
    runs, words, written, index = [], [], [], 0

    def finish():
        nonlocal words, written
        if words or written:
            runs.append((words, written))
        words, written = [], []

    while index < len(command):
        if spans.get(index, index) > index:
            written.append(command[index:spans[index]])
            index = spans[index]
            continue
        if ordinary[index] and command[index] in " \t":
            index += 1
            continue
        if ordinary[index] and command[index] == "(" and words:
            close = _closing_paren(command, index + 1)
            if close is None:
                break
            words.append(command[index:close + 1])
            runs.extend(["(", *raw_runs(command[index + 1:close]), ")"])
            index = close + 1
            continue
        end = word_end(command, index, ordinary)
        if end == index:
            finish()
            # An opener with no closer stops the walk: bash cannot parse what
            # follows it, and rescanning from each one would be quadratic.
            if command[index] in "$`":
                break
            if command[index] in "()":
                runs.append(command[index])
            index += 1
            continue
        words.append(command[index:end])
        index = end
    finish()
    return runs


def _glued_raw(raw, literal, cut):
    """The raw text of `literal[cut:]`, whose quoting still decides what expands."""
    return raw[cut:] if raw.startswith(literal[:cut]) else shlex.quote(literal[cut:])


def backup_word(word, suffix):
    """The shell word naming `word`'s `-i` backup: `*` in `suffix` is its name."""
    literal = target_literal(word)
    if literal is None or "*" not in suffix:
        return word + shlex.quote(suffix)
    named = suffix.replace("*", os.path.basename(literal))
    return shlex.quote(named if "/" in suffix
                       else os.path.join(os.path.dirname(literal), named))


def verb_destinations(verb, words):
    """The raw words `verb` writes, each with the sources a copy puts inside it.

    A hard link makes its source writable under another name, so its sources
    count; a symbolic link is judged where it lands; a `-i` backup is a write.
    """
    literals = [target_literal(word) for word in words]
    model = WRITING_VERBS[verb]
    if model == "of":
        return [(_glued_raw(raw, literal, 3), None, False)
                for raw, literal in zip(words, literals)
                if literal and literal.startswith("of=")]

    operands, targets, suffixes = [], [], []
    in_place = script_given = directory_mode = options_done = False
    symbolic = hard_link = no_target = False
    short_values = SHORT_VALUE_OPTIONS.get(verb, "")
    long_values = LONG_VALUE_OPTIONS.get(verb, ())
    position = 0
    while position < len(words):
        raw, literal = words[position], literals[position]
        position += 1
        if literal is None or options_done or literal in ("-", "") or (
                not literal.startswith("-")):
            operands.append(raw)
            continue
        if literal == "--":
            options_done = True
            continue
        if literal.startswith("--"):
            name, equals, _ = literal.partition("=")

            def abbreviates(option):
                return len(name) > 2 and option.startswith(name)

            if verb in TARGET_OPTION_VERBS and abbreviates("--no-target-directory"):
                no_target = True
            elif verb in TARGET_OPTION_VERBS and abbreviates("--target-directory"):
                if equals:
                    targets.append(_glued_raw(raw, literal, len(name) + 1))
                elif position < len(words):
                    targets.append(words[position])
                    position += 1
            elif verb == "sed" and abbreviates("--in-place"):
                in_place = True
                if equals:
                    suffixes.append(literal[len(name) + 1:])
            elif verb == "ln" and abbreviates("--symbolic") and not abbreviates("--suffix"):
                symbolic = True
            elif verb == "cp" and abbreviates("--link"):
                hard_link = True
            elif verb == "install" and abbreviates("--directory"):
                directory_mode = True
            else:
                if verb == "sed" and (abbreviates("--expression") or abbreviates("--file")):
                    script_given = True
                if not equals and any(abbreviates(option) for option in long_values):
                    position += 1
            continue
        letters = literal[1:]
        for offset, letter in enumerate(letters):
            rest = letters[offset + 1:]
            if verb in ("sed", "perl") and letter == "i":
                in_place = True
                if rest:
                    suffixes.append(rest)
                break
            if verb == "ln" and letter == "s":
                symbolic = True
                continue
            if verb == "cp" and letter == "l":
                hard_link = True
                continue
            if verb in TARGET_OPTION_VERBS and letter == "T":
                no_target = True
                continue
            if verb in TARGET_OPTION_VERBS and letter == "t":
                if rest:
                    targets.append(_glued_raw(raw, literal, offset + 2))
                elif position < len(words):
                    targets.append(words[position])
                    position += 1
                break
            if verb == "install" and letter == "d":
                directory_mode = True
                continue
            script_letter = (verb == "sed" and letter in "ef") or (
                verb == "perl" and letter in "eE")
            if script_letter:
                script_given = True
            if script_letter or letter in short_values:
                if not rest:
                    position += 1
                break
            if verb == "perl" and letter in "CDFIMdmx":
                break

    whole_list = model == "every" or directory_mode or hard_link or (
        verb == "ln" and not symbolic)
    if model != "in-place":
        if verb in TARGET_OPTION_VERBS and not directory_mode and (
                targets or len(operands) > 1):
            sources = operands if targets else operands[:-1]
            landed = [(word, None if no_target else sources,
                       bool(targets) or len(sources) > 1)
                      for word in (targets or operands[-1:])]
            return [(word, None, False) for word in sources] * whole_list + landed
        return [(word, None, False) for word in operands + targets] * whole_list
    if not in_place:
        return []
    files = operands if script_given else operands[1:]
    return [(word, None, False) for word in files + [
        backup_word(word, suffix) for suffix in suffixes for word in files]]


def landings(destination, sources, into, cwd, command):
    """The names a copy or move gives `sources` inside `destination`, or `[None]`.

    `None` is the destination itself: it is a file, or a source's name is not
    known, as for a glob or a brace. A source of `.` or `..` fills the directory whole.
    """
    literal = target_literal(destination) or ""
    path = written_path(destination, cwd, command)
    if not (into or literal.endswith(("/", "\\")) or (path and os.path.isdir(path))):
        return [None]
    names = []
    for source in sources:
        spelled = (target_literal(source) or "").rstrip("/\\")
        last = re.split(r"[\\/]", spelled)[-1]
        found = written_path(source, cwd, command)
        if last in ("", ".", "..") or found is None or any(
                mark in spelled for mark in "*?[{"):
            return [None]
        names.append(os.path.basename(found))
    return names or [None]


def find_offence(words, cwd, command):
    """The reason to refuse `find`'s own words: `-delete` from a start, an output file.

    `-delete` is `rm -r` from each start, so `.`, no start and `-files0-from` are
    the checkout at most. What an `-exec` runs is that verb's own to answer for.
    """
    literals = [target_literal(word) for word in words]
    position, starts, files, deleting = 0, [], [], False
    while position < len(words) and (literals[position] or "") in (
            "-H", "-L", "-P", *FIND_VALUE_OPTIONS) or (
            position < len(words) and (literals[position] or "").startswith("-O")):
        position += 2 if literals[position] in FIND_VALUE_OPTIONS else 1
    while position < len(words) and not (
            literals[position] and literals[position][0] in "-(!),"):
        starts.append(words[position])
        position += 1
    while position < len(words):
        literal = literals[position]
        position += 1
        if literal == "-delete":
            deleting = True
        elif literal in FIND_OUTPUT_FILES and position < len(words):
            files.append(words[position])
            position += 1
        elif literal in FIND_EXECUTORS:
            while position < len(words) and literals[position] not in (";", "+"):
                position += 1
    for start in starts if deleting else ():
        refusal = (removal_offence(start, cwd, command, "`find -delete`")
                   or destination_offence(start, cwd, command, "`find -delete`", True))
        if refusal is not None:
            return refusal
    for word in files:
        refusal = destination_offence(word, cwd, command, "`find`")
        if refusal is not None:
            return refusal
    return None


def verb_offence(verb, operands, cwd, command, writer):
    """The reason to refuse writing verb `verb` with `operands`, or `None`."""
    for word, sources, into in verb_destinations(verb, operands):
        refusal = removal_offence(word, cwd, command, writer) if (
            sources is None and verb in REMOVING_VERBS) else None
        if refusal is not None:
            return refusal
        names = [None] if sources is None or verb not in TREE_VERBS else (
            landings(word, sources, into, cwd, command))
        for name in names:
            refusal = destination_offence(word, cwd, command, writer,
                                          verb in TREE_VERBS, name)
            if refusal is not None:
                return refusal
    return None


def writing_verb_offence(words, cwd, command):
    """The reason to refuse a writing verb in the run `words`, or `None`.

    Anywhere in the run, since a wrapper puts it mid-run (`sudo tee`, `env cp`,
    `find -exec cp`); a run led by a reader or a printer cannot run those words.
    """
    literals = [target_literal(word) for word in words]
    if program_name(leading_command([word or "" for word in literals])) in (
            READING_COMMANDS | DATA_ONLY_COMMANDS):
        return None
    for index, literal in enumerate(literals):
        if literal is None or ASSIGNMENT.match(words[index]):
            continue
        verb = program_name(literal)
        if verb == "find":
            refusal = find_offence(words[index + 1:], cwd, command)
            if refusal is not None:
                return refusal
        if verb not in WRITING_VERBS:
            continue
        operands = words[index + 1:]
        if any(word in FIND_EXECUTORS for word in literals[:index]):
            for cut, word in enumerate(literals[index + 1:]):
                if word in (";", "+"):
                    operands = operands[:cut]
                    break
        refusal = verb_offence(verb, operands, cwd, command, f"`{verb}`")
        if refusal is not None:
            return refusal
    return None


def _git_operands(words, literals, value_options):
    """`(options, before, after)` of a git verb's words: `after` follows a `--`."""
    options, before, after, position = [], [], None, 0
    while position < len(words):
        raw, literal = words[position], literals[position]
        position += 1
        # A word holding a substitution has no literal, and is an option all the same.
        spelled = literal if literal is not None else raw.strip("\"'")
        if after is not None:
            after.append(raw)
        elif literal == "--":
            after = []
        elif spelled.startswith("-") and spelled != "-":
            options.append(spelled)
            if literal in value_options:
                if position < len(words):
                    options.append(literals[position] or "$")
                position += 1
        else:
            before.append(raw)
    return options, before, after or []


def _has_short_letter(options, letter):
    """Whether a short cluster in `options` carries `letter`."""
    return any(re.fullmatch(rf"-[A-Za-z]*{letter}[A-Za-z]*", option) for option in options)


def _restore_source(options):
    """The `--source` of `git restore`'s `options`, spelled any way git takes it."""
    for index, option in enumerate(options):
        name, equals, value = option.partition("=")
        if name.startswith("--") and len(name) > 3 and "--source".startswith(name):
            return value if equals else (options[index + 1] if index + 1 < len(options) else "")
        cluster = re.fullmatch(r"-[A-Za-z]*s(.*)", option)
        if cluster and not option.startswith("--"):
            return cluster.group(1) or (options[index + 1] if index + 1 < len(options) else "")
    return None


def git_path_offence(words, cwd, command):
    """The reason to refuse a git verb writing a denied path, or `None`.

    `git rm`, `git mv` and a restore from another commit, from a literal `-C`. A
    restore from `HEAD` puts the file back, and a worktree's write is in its diff.
    """
    literals = [target_literal(word) for word in words]
    lead = leading_command([word or "" for word in literals])
    if program_name(lead) != "git":
        return None
    position = literals.index(lead) + 1
    while position < len(words) and (literals[position] or "").startswith("-"):
        if literals[position] == "-C" and position + 1 < len(words):
            cwd = written_path(words[position + 1], cwd, command)
        position += 2 if literals[position] in GLOBAL_VALUE_FLAGS else 1
    if position >= len(words) or cwd is None or in_linked_worktree(os.path.abspath(cwd)):
        return None
    verb, words, literals = literals[position], words[position + 1:], literals[position + 1:]
    if verb in ("rm", "mv"):
        options, _, _ = _git_operands(words, literals, ())
        if set(options) & {"--cached", "--dry-run"} or _has_short_letter(options, "n"):
            return None
        return verb_offence(verb, words, cwd, command, f"`git {verb}`")
    if verb == "checkout":
        options, before, after = _git_operands(
            words, literals, {"-b", "-B", "--orphan", "--conflict", "--pathspec-from-file"})
        if set(options) & {"-b", "-B", "--orphan"}:
            return None
        # A source nothing can read is judged by its paths, which can be read.
        source = (target_literal(before[0]) or "$") if before else None
        # As git disambiguates: with no `--`, a first word naming a path is one.
        first = written_path(before[0], cwd, command) if before and not after else None
        if first is not None and os.path.lexists(first):
            source = None
        paths = after or before[1:]
    elif verb == "restore":
        options, before, after = _git_operands(
            words, literals, {"-s", "--source", "--conflict", "--pathspec-from-file", "-U", "--unified"})
        source = _restore_source(options)
        # What follows a short `s` is its value: `-sStaging` is no `--staged`.
        letters = [option.split("s", 1)[0] for option in options]
        staged = "--staged" in options or _has_short_letter(letters, "S")
        worktree = "--worktree" in options or _has_short_letter(letters, "W")
        if staged and not worktree:
            return None
        paths = before + after
    else:
        return None
    if source is None or source in ("HEAD", "@"):
        return None
    for word in paths:
        refusal = destination_offence(word, cwd, command, f"`git {verb}` from `{source}`", True)
        if refusal is not None:
            return refusal
    return None


def directory_after(words, cwd, command):
    """Where the run `words` leaves the shell: `cwd`, or `None` if unreadable."""
    literals = [target_literal(word) for word in words]
    lead = leading_command([word or "" for word in literals])
    if not lead:
        return cwd
    start = literals.index(lead)
    if program_name(lead) in DIRECTORY_WRAPPERS and start + 1 < len(literals):
        start += 1
    verb = program_name(literals[start] or "")
    if verb not in DIRECTORY_CHANGES:
        return cwd
    operands = [word for word, literal in zip(words[start + 1:], literals[start + 1:])
                if literal is None or not literal.startswith("-") or literal == "-"]
    if verb == "popd" or target_literal(operands[0] if operands else "~") == "-":
        return None
    return written_path(operands[0] if operands else "~", cwd, command)


def write_offence(command):
    """The reason to refuse a write in `command`, or `None`.

    A relative target is placed by following each literal `cd`, and after a
    `cd` nothing can read, only an absolute target is judged.
    """
    cwd = EVENT_CWD or os.getcwd()
    saved = []
    for run in raw_runs(command):
        if run == "(":
            saved.append(cwd)
            continue
        if run == ")":
            cwd = saved.pop() if saved else cwd
            continue
        words, written = run
        for text in written:
            match = WRITING_REDIRECTION.match(text)
            if match is None:
                continue
            target = match.group("target").strip()
            if not target or (match.group("operator") == ">&"
                              and (target == "-" or target.isdigit())):
                continue
            refusal = destination_offence(
                target, cwd, command, f"`{match.group('operator')}`")
            if refusal is not None:
                return refusal
        refusal = (git_path_offence(words, cwd, command)
                   or writing_verb_offence(words, cwd, command))
        if refusal is not None:
            return refusal
        cwd = directory_after(words, cwd, command)
    return None


# Substitutions and evaluators both recurse, and a crafted nest of either would
# otherwise reach Python's own limit — where the hook dies with a traceback
# rather than a verdict, which is the one direction a guard must not fail in.
MAX_NESTING = 24


def offence(command, depth=0, judged=None, reading=False):
    """The reason to refuse `command`, or None to allow it.

    `judged` is a verdict cache, and it is what keeps the cost finite. Each
    reading and each extracted substitution recurses onto a string barely
    shorter than the one it came from, so a command nesting them multiplies —
    a short `$( echo ${a:-{z,X}} )` nest runs past the hook timeout, which
    produces no verdict, which `PreToolUse` treats as non-blocking.

    The cache holds the verdict rather than the visit: remembering only that a
    string had been seen would return None the second time a refusing string
    appeared, and lose the refusal. A string reached inside its own evaluation is recorded as None
    first, so a cycle terminates without inventing a verdict — the outer call
    is the one that answers.
    """
    if judged is None:
        judged = {}
    if command in judged:
        return judged[command]
    judged[command] = None

    verdict = _offence(command, depth, judged, reading)
    judged[command] = verdict
    return verdict


def _offence(command, depth, judged, reading):
    """`offence`'s body, called only through its cache."""
    if depth > MAX_NESTING:
        return (
            "this command nests shells or substitutions more deeply than the "
            "guard will follow; refusing rather than reading part of it."
        )

    if undecodable_heredoc(command):
        return (
            "a heredoc names a delimiter this guard cannot decode, so it "
            "cannot tell where the body ends or which lines after it are "
            "commands; refusing rather than reading part of it."
        )


    # An expansion has more than one reading, and the command is safe only if
    # it is safe under all of them: an empty substitution joins the words
    # around it, whitespace splits one into several, a default puts its own
    # text on the line, and a single-element brace range is the text inside it.
    # Each is what bash does in the shell these commands run in — no positional
    # parameters, no variables set — so none is the run-time residual
    # `docs/harness-boundaries.md` names; the dangerous string is in the source.
    for description, rewrite in (
        ("a command substitution taken as empty", without_substitutions),
        ("an expansion taken as whitespace", splitting_expansions),
        ("an expansion taken as its default", defaulted_expansions),
        ("a brace expansion taken as one word", brace_expanded),
    ):
        variant = outside_verbatim(command, rewrite)
        if variant != command:
            refusal = offence(variant, depth + 1, judged, reading=True)
            if refusal is not None:
                return f"with {description}: {refusal}"

    if substitution_fed_shells(command):
        return (
            "a shell is handed its script by a process substitution, so what "
            "it runs is that command's output rather than anything written "
            "here; refusing rather than judging the source instead of the "
            "result."
        )

    for script in stdin_scripts(command):
        # A substitution inside a script a shell will run can supply the
        # command itself — `bash <<<"$(printf git) push origin +HEAD:main"` —
        # and the text that decides is not in the source, so this refuses on
        # `unmodelled_printer`'s argument.
        if substitutions(script):
            return (
                "a script handed to a shell on stdin builds part of itself "
                "with a command substitution, so what that shell runs cannot "
                "be read; refusing rather than judging the source instead of "
                "the result." + STDIN_DATA_ROUTE
            )
        refusal = offence(script, depth + 1, judged)
        if refusal is not None:
            return f"in a script handed to a shell on stdin: {refusal}" + STDIN_DATA_ROUTE

    for text, quotes in expandable_regions(command):
        # The continuation join happens before anything looks for a
        # substitution: bash removes `\<newline>` inside double quotes too, so
        # `git log "$\<newline>(git push origin +HEAD:main)"` has a live `$(`.
        # A heredoc body arrives with `quotes` false and is not a command line.
        text = join_continuations(text, quotes=quotes)
        for inner in substitutions(text, quotes=quotes):
            refusal = offence(inner, depth + 1, judged)
            if refusal is not None:
                return f"inside a command substitution: {refusal}"

    # Stripped once, and used by both paths below, because a heredoc body and
    # a comment are data on every path, not only on the one that parses.
    #
    # `strip_redirections` is outermost because a redirection inside a heredoc
    # body or a comment is not one bash performs. `join_continuations` sits
    # after `strip_comments` because a backslash at the end of a comment
    # continues nothing — bash ends a comment at the newline.
    performed = separate_lines(
        join_continuations(strip_comments(strip_heredocs(command))))
    resolved = strip_redirections(performed)

    # The check and the code that acts on it read the same string: on the raw
    # command, a heredoc body or comment mentioning `$'\n'` would be refused,
    # and `git $\<newline>'\x70ush' origin +HEAD:main`, whose sigil and quote a
    # continuation separates, would show nothing to refuse while
    # `strip_dollar_quotes`, after the join, finds the quote.
    unreadable = unreadable_dollar_quote(resolved)
    if unreadable is not None:
        return unreadable

    # `strip_dollar_quotes` turns `$'…'` and `$"…"` into the ordinary quoting
    # `shlex` resolves, on the string just checked.
    stripped = strip_dollar_quotes(resolved)
    try:
        lexer = shlex.shlex(stripped, posix=True, punctuation_chars=True)
        # Comments are already gone, and `shlex` would take a second, wider view
        # of them: its `commenters` fires mid-word, where bash's fires only at
        # the start of one (`strip_comments`).
        lexer.commenters = ""
        lexer.whitespace_split = True
        tokens = list(lexer)
    except ValueError:
        # Unparseable is refused below, whatever it is; the checks before that
        # only give the refusal a sharper reason.
        # Bash removes quotes and backslashes too, so `pu''sh` is still push here.
        flat = re.sub(r"[\\'\"]", "", stripped)
        for needle in FORBIDDEN_FLAGS + FORBIDDEN_SUBSTRINGS:
            if needle in flat:
                return (
                    f"`{needle}` appears in a command this guard could not "
                    "tokenise; refusing on the string with its quotes removed, "
                    "a cruder check than the parsed one."
                )
        # An unterminated heredoc leaves its body in the string, so a quote in
        # the body is what failed the parse, and the checks below would read the
        # body as commands; refused outright, as an undecodable delimiter is.
        unclosed = []
        heredoc_spans(command, unclosed)
        if unclosed:
            return (
                "a heredoc here has no closing delimiter and the command "
                "cannot be tokenised, so what bash runs cannot be read; "
                "refusing rather than judging its body as commands."
            )
        # Everything left is refused: a line is easy to make untokenisable on
        # purpose, bash runs the lines before the one that breaks, and a glob,
        # an expansion or another case can name git without its letters.
        return (
            "this command could not be tokenised, so what it runs cannot be "
            "read; refusing rather than admitting a command nothing checked. "
            "Balance its quotes, or move a body into a quoted heredoc."
        )

    if unmodelled_printer(tokens):
        return (
            "a printer whose output this guard cannot reproduce writes into a "
            "shell, so what that shell runs cannot be read; refusing rather "
            "than judging the arguments instead of the bytes."
        )

    for script in evaluated_scripts(tokens):
        refusal = offence(script, depth + 1, judged)
        if refusal is not None:
            return f"inside a shell evaluator: {refusal}"

    if unchecked_push_pipeline(tokens):
        return UNCHECKED_PUSH

    if xargs_supplies_push(tokens):
        return (
            "`xargs` supplies this `git push` with arguments read from stdin, "
            "so its destination is not in the command and cannot be judged. "
            "Name the remote and the refspec."
        )

    for segment in git_segments(tokens):
        refusal = (push_offence(segment) or trap_offence(segment)
                   or fetch_offence(segment) or pull_offence(segment)
                   or refspec_offence(segment) or config_offence(segment))
        if refusal is not None:
            return refusal

        for element in global_options(segment):
            # A compact `-c<name>=<value>` is refused as hardening: git's usage
            # spells the option `-c <name>=<value>`. It is cheap because this
            # loop sees only the tokens before the subcommand, where no
            # subcommand flag can reach. The comparison is case-sensitive so
            # `-C` is left alone.
            if element in CONFIG_OPTIONS or element.startswith("-c") or any(
                    element.startswith(option + "=") for option in CONFIG_OPTIONS):
                return (
                    "`git -c` / `--config-env` sets configuration for one "
                    "invocation, and git EXECUTES several config keys — "
                    "`alias.*`, `core.pager`, `core.sshCommand`, "
                    "`core.hooksPath` and more. Nothing here passes one, so "
                    "the option is refused rather than its value guessed at."
                )

        subcommand = subcommand_of(segment)
        if subcommand in REFUSED_SUBCOMMANDS:
            return (
                f"`git {subcommand}` {REFUSED_SUBCOMMANDS[subcommand]}, and "
                "nothing here runs it, so it is refused whole."
            )
        operand = EXECUTING_FORMS.get(subcommand)
        if operand is not None and operand in after_global_options(segment)[1:]:
            return (
                f"`git {subcommand} {operand}` runs the command it is given, "
                "which no flag check can see, and nothing here runs it."
            )
        if subcommand == "subtree" and "push" in after_global_options(segment)[1:]:
            return (
                "`git subtree push` updates a remote ref past every check a "
                "`git push` gets, and nothing here runs it."
            )
        value_flags = VALUE_FLAGS_BY_SUBCOMMAND.get(subcommand, frozenset())
        skip = False
        for element in segment:
            if skip:
                skip = False
                continue
            if element in value_flags:
                skip = True
                continue
            # Git accepts any unambiguous abbreviation of a long option —
            # `git fetch --upl=<cmd>` runs the command — so the test runs both
            # ways: the element starting with a forbidden flag, and a forbidden
            # flag starting with the element. An abbreviation of something
            # harmless that prefixes one of these is refused too, which is the
            # direction to be wrong in.
            name = element.split("=", 1)[0]
            abbreviation = (name.startswith("--") and len(name) > 2
                            and name not in WHOLE_OPTIONS)
            for flag in FORBIDDEN_FLAGS + EXECUTING_LONG_FLAGS.get(subcommand, ()):
                whole = (element.startswith(flag) if flag in FORBIDDEN_FLAGS
                         else name == flag)
                if whole or (abbreviation and flag.startswith(name)):
                    return (
                        f"`git ... {flag}` is refused: it writes or executes "
                        "rather than inspects, and the settings deny it matches "
                        "only the unquoted spelling. This hook compares the "
                        "resolved argv, and any unambiguous abbreviation of it."
                    )
            letter = short_flag(element, subcommand,
                                EXECUTING_SHORT_FLAGS.get(subcommand, ""))
            if letter is not None:
                return (
                    f"`git {subcommand} -{letter}` is the short spelling of a "
                    "flag that runs a command, bundled or not, and the long "
                    "spelling is refused too."
                )
            if subcommand not in REPOSITORY_SUBCOMMANDS:
                continue
            for substring in FORBIDDEN_SUBSTRINGS:
                if substring in element:
                    return (
                        f"`{substring}` is a git transport that runs its "
                        "argument as a command, and no Bash permission rule can "
                        "express it."
                    )
    # A reading of an expansion hides a program; it names no file bash opens.
    return None if reading else write_offence(performed)


# Every scan here must finish inside the hook's timeout, because a hook that
# times out is non-blocking. The scanners as a whole grow faster than
# linearly in the command's length: measured here, 200,000 plain characters
# took 6 seconds and 1,000,000 took 58, against a default timeout of 60. A
# command past `LENGTH_BUDGET` is refused before any scan runs, which fails
# closed where the timeout would fail open.
LENGTH_BUDGET = 100_000


def scan_budget(command):
    """The refusal for a command the scanners could not finish in time."""
    if len(command) <= LENGTH_BUDGET:
        return None
    return (
        f"this command's {len(command)} characters are more than this guard "
        "can judge inside its time limit, and a guard that times out admits "
        "the command, so it is refused instead. Shorten it or split it into "
        "separate commands."
    )


# PowerShell is a language `offence` does not parse, so a command through that
# tool is admitted only as one read-only cmdlet over plain words: no quote,
# escape, variable, call, pipe, separator or wildcard, and no launcher, whose
# arguments (an encoded command, say) can carry git in characters this allows.
POWERSHELL_PLAIN = re.compile(r"[A-Za-z0-9 _./:,=-]*")
POWERSHELL_GIT = re.compile(r"(?i)(?<![a-z0-9_])git(?![a-z0-9_])")
POWERSHELL_READS = frozenset((
    "get-childitem", "get-content", "get-item", "get-location", "resolve-path",
    "select-string", "test-path", "write-output",
))


def powershell_git(command):
    """The refusal for a PowerShell command that is not one read-only cmdlet over plain words, or None."""
    words = command.split()
    if (words and words[0].lower() in POWERSHELL_READS and POWERSHELL_PLAIN.fullmatch(command)
            and not POWERSHELL_GIT.search(command)):
        return None
    return (
        "a PowerShell command is admitted only as one read-only cmdlet over "
        "plain words: this guard reads Bash's grammar, not PowerShell's, so a "
        "launcher, quote, escape, variable, call or separator could run git "
        "unseen. Run git, and anything else, through the Bash tool."
    )


def refuse(reason):
    """Print the deny JSON for `reason`; the exit status stays 0 either way."""
    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            }
        },
        sys.stdout,
    )
    return 0


def main():
    # The bytes, decoded as UTF-8: Python reads a Windows pipe in the ANSI code
    # page, which raises on bytes UTF-8 uses, and that raise reached the
    # fail-open below. An event that is not UTF-8 holds a command nothing read.
    try:
        text = sys.stdin.buffer.read().decode("utf-8")
    except UnicodeDecodeError:
        return refuse(
            "this hook event is not UTF-8, so the command in it cannot be "
            "read; refusing rather than admitting what nothing judged.")
    try:
        event = json.loads(text)
    except (json.JSONDecodeError, ValueError):
        # A hook that cannot read its own input has established nothing. Say so
        # and allow: refusing every Bash call on a malformed event would take
        # the session down for a defect in this file.
        print("guard-git-argv: unreadable hook event; not judging", file=sys.stderr)
        return 0

    tool = event.get("tool_name")
    if tool not in ("Bash", "PowerShell"):
        return 0

    global EVENT_CWD
    cwd = event.get("cwd")
    EVENT_CWD = cwd if isinstance(cwd, str) and cwd else None

    command = (event.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        return 0

    try:
        reason = (powershell_git(command) if tool == "PowerShell"
                  else scan_budget(command) or offence(command))
    except Exception:  # noqa: BLE001 - the direction is the point
        # A crash is empty stdout, and `PreToolUse` reads empty stdout as
        # non-blocking, so an uncaught defect here would be a fail-open.
        #
        # This differs from the malformed-event case above on purpose: an
        # unreadable event says nothing about any command, so refusing there
        # would stop the session for a defect in this file, while a crash
        # judging this command says this command broke the parser, and
        # refusing one command is proportionate.
        traceback.print_exc(file=sys.stderr)
        reason = (
            "this command crashed the guard that judges it, so nothing about "
            "it has been established; refusing rather than admitting what "
            "could not be read. The traceback is on stderr."
        )
    if reason is None:
        return 0

    return refuse(reason)


if __name__ == "__main__":
    sys.exit(main())
