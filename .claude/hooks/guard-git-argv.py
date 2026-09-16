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

import json
import re
import shlex
import sys
import traceback

# Flags that write or execute rather than inspect. Matched on a prefix, so
# `--exec-path=<dir>` — a directory of binaries for git to run — is the same act
# as `--exec`.
FORBIDDEN_FLAGS = ("--output", "--upload-pack", "--receive-pack", "--exec")

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
REPOSITORY_SUBCOMMANDS = {
    "fetch", "clone", "pull", "push", "remote", "submodule", "ls-remote",
    "archive", "bundle",
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
}

SEPARATORS = {"&&", "||", ";", "|", "&", "(", ")", "{", "}", "\n"}

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
# separator, nothing that could be a pattern or an option.
SAFE_REF = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._/-]*$")
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


def _quoting(command, quotes=True, data=()):
    """Walk `command`, yielding `(index, state, escaped)` per character.

    `data` is spans this scanner must read as data rather than as shell text —
    heredoc bodies, and only `heredoc_spans` passes any. Inside one, a quote
    opens nothing and a `#` starts nothing: the characters are yielded as
    quoted, which is what every consumer of this scanner means by "not a
    command line".

    One scanner for heredoc openers and substitution parens alike: a regex
    search for `<<` finds an opener inside a comment, so `git status # <<EOF`
    would swallow the command on the next line, and a paren counter that
    ignores quotes closes `git log "$(printf ')'; git push origin +HEAD:main)"`
    early, hiding the push in the outer token.

    `state` is `"single"` inside `'…'`, `"double"` inside `"…"`, `"comment"`
    in a comment, `"data"` inside one of `data`'s spans and `""` elsewhere.
    Comments start at an unquoted `#` that begins a word and end at the
    newline — which is bash's rule, and the reason `git log --grep=#x` is not
    a comment.

    An ANSI-C word takes a backslash and an ordinary single-quoted one does
    not, so `$'…'` is read by its own rule. Read by the ordinary one, an
    escaped quote closes the word, the next quote opens one that never closes,
    and the rest of the line — `2>&1 push origin +HEAD:main`, say — reads as
    quoted to every consumer of this scanner. `$"…"` needs nothing, because a
    locale-quoted word already follows the double-quoted rule this scanner
    applies to it.
    """
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
    index = 0
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


def heredoc_spans(command):
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
    for index, in_quotes, in_comment in shell_positions(command, data):
        if in_quotes or in_comment:
            continue
        if not command.startswith("<<", index):
            continue
        # `<<<` is a here-string: `cat <<<EOF` passes the word `EOF` on stdin
        # and the next line is an ordinary command, so matching `<<EOF` at its
        # second character would swallow that line. Two tests, because the
        # operator has two ends: an index inside a run of `<` is not the start
        # of an operator, and an operator that continues past `<<` is not a
        # heredoc.
        if index > 0 and command[index - 1] == "<":
            continue
        if command.startswith("<<<", index):
            continue
        match = HEREDOC.match(command, index)
        if not match:
            continue
        # One parse of the delimiter word, quote removal included —
        # `<<\EOF` is a quoted delimiter to bash the same way `<<'EOF'`
        # is, and `<<E"OF"` is one in parts.
        delimiter, expands = _heredoc_delimiter(match.group("word"))
        if delimiter is None:
            # A delimiter this file cannot decode opens no body, so the
            # lines after it stay commands and are judged as such.
            continue
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
            continue

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
        if closing is None:
            # No span, so nothing is stripped. A delimiter this guard cannot
            # find means either an unterminated heredoc, whose tail is data and
            # over-refuses, or a delimiter read wrongly, whose tail holds
            # commands. Scanning the tail is wrong only in the safe direction.
            break
        pending = start + closing.end()
        spans.append((start, pending, expands))
        data.append((start, pending))
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
    """Whether a heredoc names a delimiter this file cannot read.

    An undecodable delimiter is refused outright, because leaving its body to
    be judged as commands holds only while the command still tokenises: a body
    carrying an unmatched quote sends `offence` down its `ValueError` path,
    which does not enforce the push allow-list, so a force push after
    `git commit -F - <<$'E\\x4fF'` would pass. Decoding every ANSI-C escape
    instead is a list that trails bash's, and each gap in it reopens this.

    The scan asks `shell_positions` where the `<<` is, so a delimiter quoted
    inside an argument is not one of these; the two guards below are
    `heredoc_spans`', for the same reasons it states.
    """
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
        close = _closing_brace(command, start + 2)
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


def leading_command(run):
    """The command word of `run`, past any assignment prefix.

    `X=1 bash` is a run led by `bash`, so
    `X=1 bash <<<'git push origin +HEAD:main'` reads its here-string as a
    script. The same reading is owed to the printer half — `X=1 echo … | bash`
    — and to the data-only exemption, which is why this is one function rather
    than a test repeated at each site.
    """
    for token in run:
        if not ASSIGNMENT.match(token):
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
    while start > 0 and not (
            ordinary[start - 1] and command[start - 1] in RUN_SEPARATORS):
        start -= 1
    end = position
    while end < len(command) and not (
            ordinary[end] and command[end] in RUN_SEPARATORS):
        end += 1
    return start, end


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
        while index < len(command) and not (
                ordinary[index] and command[index] in RUN_SEPARATORS):
            index += 1
        if reads_stdin_as_script(_run_words(command, start, index, ordinary)):
            return True
    return False


def _consumes_as_script(command, position, ordinary):
    """Whether the script at `position` is executed by its own run or a later
    one in the same pipeline."""
    start, end = _run_bounds(command, position, ordinary)
    return (reads_stdin_as_script(_run_words(command, start, end, ordinary))
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
    spans = heredoc_spans(command)
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
    # `heredoc_spans` yields its spans in opener order and skips an opener
    # inside an earlier body, so the pairing walks both lists together.
    openers = [
        match.start() for match in HEREDOC.finditer(command)
        if ordinary[match.start()]
        and not (match.start() > 0 and command[match.start() - 1] == "<")
        and not command.startswith("<<<", match.start())
    ]
    # Two monotonic cursors rather than a containment test, which is quadratic
    # in heredocs and can reach the hook timeout, whose empty stdout is
    # non-blocking. An opener inside a body is not in this list at all: the
    # scan above is told where the bodies are, so it reports one as quoted.
    cursor = 0
    for start, end, _expands in spans:
        while cursor < len(openers) and openers[cursor] >= start:
            cursor += 1
        if cursor >= len(openers):
            break
        opener = openers[cursor]
        cursor += 1
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
    if destination.startswith("refs/heads/"):
        destination = destination[len("refs/heads/"):]
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


# Substitutions and evaluators both recurse, and a crafted nest of either would
# otherwise reach Python's own limit — where the hook dies with a traceback
# rather than a verdict, which is the one direction a guard must not fail in.
MAX_NESTING = 24


def offence(command, depth=0, judged=None):
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

    verdict = _offence(command, depth, judged)
    judged[command] = verdict
    return verdict


def _offence(command, depth, judged):
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
    for description, reading in (
        ("a command substitution taken as empty", without_substitutions),
        ("an expansion taken as whitespace", splitting_expansions),
        ("an expansion taken as its default", defaulted_expansions),
        ("a brace expansion taken as one word", brace_expanded),
    ):
        variant = outside_verbatim(command, reading)
        if variant != command:
            refusal = offence(variant, depth + 1, judged)
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
                "the result."
            )
        refusal = offence(script, depth + 1, judged)
        if refusal is not None:
            return f"in a script handed to a shell on stdin: {refusal}"

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
    resolved = strip_redirections(
        separate_lines(
            join_continuations(strip_comments(strip_heredocs(command)))))

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
        # Unparseable is not hostile: `shlex` fails on commands bash runs, an
        # ordinary commit among them. A parse failure degrades to the substring
        # scan the settings deny already performs, never a silent pass.
        for needle in FORBIDDEN_FLAGS + FORBIDDEN_SUBSTRINGS:
            if needle in stripped:
                return (
                    f"`{needle}` appears in a command this guard could not "
                    "tokenise; refusing on the raw string, which is the weaker "
                    "check the settings deny already performs."
                )
        # The push check reaches this path too, because a line is easy to make
        # untokenisable on purpose; here it can only be the crude one.
        if re.search(r"\bgit\b[^;&|\n]*\bpush\b", stripped):
            return (
                "a `git push` appears in a command this guard could not "
                "tokenise, so its remote and refspec cannot be read; refusing "
                "rather than admitting a push nothing checked."
            )
        return None

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

    for segment in git_segments(tokens):
        refusal = push_offence(segment)
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
            abbreviation = name.startswith("--") and len(name) > 2
            for flag in FORBIDDEN_FLAGS:
                if element.startswith(flag) or (
                        abbreviation and flag.startswith(name)):
                    return (
                        f"`git ... {flag}` is refused: it writes or executes "
                        "rather than inspects, and the settings deny it matches "
                        "only the unquoted spelling. This hook compares the "
                        "resolved argv, and any unambiguous abbreviation of it."
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
    return None


def main():
    try:
        event = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        # A hook that cannot read its own input has established nothing. Say so
        # and allow: refusing every Bash call on a malformed event would take
        # the session down for a defect in this file.
        print("guard-git-argv: unreadable hook event; not judging", file=sys.stderr)
        return 0

    if event.get("tool_name") != "Bash":
        return 0

    command = (event.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        return 0

    try:
        reason = offence(command)
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


if __name__ == "__main__":
    sys.exit(main())
