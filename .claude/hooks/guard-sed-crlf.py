#!/usr/bin/env python3
"""Refuse a `sed -i` that would rewrite a tracked CRLF file with LF endings.

Git Bash's sed drops the CR from every line it writes in place; the argument, what this hook reads and where it fails
open are `docs/harness-boundaries.md`'s (*The sed line-ending guard*)."""

import glob
import itertools
import json
import os
import re
import shlex
import subprocess
import sys
import traceback
import types
from pathlib import Path


def path_grammar():
    """`path_grammar.py` beside this file, which names a path on another machine; compiled, so no bytecode is left."""
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "path_grammar.py")
    module = types.ModuleType("path_grammar")
    module.__file__ = path
    with open(path, encoding="utf-8") as handle:
        exec(compile(handle.read(), path, "exec"), module.__dict__)
    return module


def _loaded_grammar():
    """The grammar, or `None` and why: a sibling broken mid-edit must reach main()'s refusal, not crash the import."""
    try:
        return path_grammar(), None
    except Exception:  # noqa: BLE001
        return None, traceback.format_exc()


GRAMMAR, GRAMMAR_FAILURE = _loaded_grammar()

# What a refusal names as the forms that keep the file's endings. `--binary` is admitted: measured under Git Bash's
# GNU sed 4.9, `sed --binary -i` writes the CR back where a plain `-i` drops it.
SAFE_FORMS = (
    "Edit it with the Edit tool, or with a Python script that opens it with newline=\"\" and writes its own line "
    "ending back; `sed --binary -i` keeps the CR and is admitted."
)

# `shlex` returns a run of these as one token, `;\n` or `>&` alike, so a token is a redirection when it holds `<` or
# `>` and a separator between commands otherwise. A newline is one of them, so `shlex` is told it is not whitespace.
PUNCTUATION = ";&|()<>\n"

# Words that run the command after them, so `sed` behind one is still the command: the reserved words that open a
# group or a clause, as in `if …; then sed -i …; fi`. They take no options.
PREFIXES = {
    "{", "!", "if", "then", "elif", "else", "while", "until", "do",
}

# Programs and prefix commands that run the command after their own options, each with the options that take a
# separate argument; `time -p` or `command -p` before `sed` leaves `sed` the command.
WRAPPERS = {
    "command": set(),
    "exec": {"-a"},
    # bash's `time` keyword takes `-p` alone; GNU time, run as a program, a format and an output file.
    "time": {"-f", "--format", "-o", "--output"},
    "builtin": set(),
    "nohup": set(),
    "env": {"-u", "--unset", "-C", "--chdir"},
    "nice": {"-n", "--adjustment"},
    "timeout": {"-s", "--signal", "-k", "--kill-after"},
    "stdbuf": {"-i", "-o", "-e", "--input", "--output", "--error"},
}

# The shells whose command line is judged as a command of its own, to `MAX_DEPTH` nested lines: a `-c` line, or a
# heredoc, a here-string or an `echo`'s words piped into one reading its input.
SHELLS = {"bash", "sh", "bash.exe", "sh.exe"}
# Stands for a heredoc body in the line `prepared` returns: a NUL, which no bash command line can carry.
BODY = "\x00"
# Bash's long options, which it reads before any cluster with one dash or two, and those that take the next word as
# their value; a short option's value is the next word for each `o` and `O` in its cluster.
SHELL_LONG = {"debug", "debugger", "dump-po-strings", "dump-strings", "help", "login", "noediting", "noprofile", "norc",
              "posix", "pretty-print", "protected", "restricted", "verbose", "version", "wordexp"}
SHELL_VALUED = {"rcfile", "init-file"}
MAX_DEPTH = 3

ASSIGNMENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*=")
# What follows a `<<`: the `-` that strips tabs, then the delimiter word, which quotes or a backslash may spell.
HEREDOC = re.compile(r"(-?)[ \t]*((?:[A-Za-z0-9_.\-]|\\.|'[^'\n]*'|\"[^\"\n]*\")+)")
MSYS_DRIVE = re.compile(r"^/([A-Za-z])(?=/|$)")
# What bash reads inside braces as a sequence: two integers or two letters, then an optional integer step.
SEQUENCE = re.compile(r"([-+]?\d+|[A-Za-z])\.\.([-+]?\d+|[A-Za-z])(?:\.\.([-+]?\d+))?")

# GNU sed's long options, matched as `getopt_long` matches them: an exact name, or a prefix only one name has.
LONG_OPTIONS = (
    "binary", "debug", "expression", "file", "follow-symlinks", "help", "in-place", "line-length", "null-data",
    "posix", "quiet", "regexp-extended", "sandbox", "separate", "silent", "unbuffered", "version",
    "zero-terminated",
)
LONG_WITH_ARGUMENT = {"expression", "file", "line-length"}
SHORT_FLAGS = set("nrsuzEb")
SHORT_WITH_ARGUMENT = set("efl")

# Bounds on the work one call does, so a glob over a large tree cannot outrun the hook: the files a glob walks to
# and the files read, the bytes read of each, and each git call.
MAX_FILES = 200
MAX_READ = 4 << 20
GIT_TIMEOUT = 5
# The directories one subshell is followed in at once: each `cd` that may not run can double them, so past the bound
# the rest are dropped, and the files they would name are unplaced.
MAX_PLACES = 16


def prepared(command):
    """The command as bash reads its lines, continuations joined and comments dropped, and its heredoc bodies.

    Each body is lifted out of the line and its opener replaced by `<< <BODY><n>`, so the body is never read as
    commands and a shell reading it can still be handed it.
    """
    out = []
    bodies = []
    pending = []
    quote = None
    word_start = True
    arithmetic = 0
    index = 0
    while index < len(command):
        character = command[index]
        if quote == "'":
            out.append(character)
            index += 1
            quote = None if character == "'" else quote
            continue
        if character == "\\" and index + 1 < len(command):
            if command[index + 1] != "\n":
                out.append(command[index:index + 2])
                word_start = False
            index += 2
            continue
        if quote == '"':
            out.append(character)
            index += 1
            quote = None if character == '"' else quote
            continue
        if character in "'\"":
            quote = character
            word_start = False
        elif character == "#" and word_start:
            while index < len(command) and command[index] != "\n":
                index += 1
            continue
        elif command.startswith("((", index) or (arithmetic and command.startswith("))", index)):
            # A `<<` inside `$(( ))` or `(( ))` is a shift, whatever word follows it.
            arithmetic += 1 if character == "(" else -1
            out.append(command[index:index + 2])
            index += 2
            word_start = False
            continue
        elif (not arithmetic and command.startswith("<<", index) and not command.startswith("<<<", index)
              and command[index - 1:index] != "<"):
            opener = HEREDOC.match(command, index + 2)
            delimiter = re.sub(r"[\\'\"]", "", opener.group(2)) if opener else ""
            if delimiter:
                # The opener is replaced by a word naming its body, which `segments` hands to a shell reading it.
                pending.append((opener.group(1) == "-", delimiter, len(bodies)))
                bodies.append([])
                out.append(f"<< {BODY}{len(bodies) - 1}")
                index = opener.end()
                word_start = False
                continue
        elif character == "\n":
            out.append(character)
            index += 1
            word_start = True
            while pending:
                strip_tabs, delimiter, body = pending.pop(0)
                while index < len(command):
                    end = command.find("\n", index)
                    end = len(command) if end < 0 else end
                    line = command[index:end]
                    index = end + 1
                    line = line.lstrip("\t") if strip_tabs else line
                    if line == delimiter:
                        break
                    bodies[body].append(line)
            continue
        out.append(character)
        index += 1
        if quote is None and character not in "'\"":
            word_start = character in " \t" or character in PUNCTUATION
    return "".join(out), ["\n".join(lines) for lines in bodies]


class Segment:
    """One simple command: its words, its subshells, whether it is piped or backgrounded, and any `&&` or `||` before.

    These say whether a `cd` in it moves the commands after it: bash runs a `( … )` group, a pipeline stage and a
    command a lone `&` ends in a subshell, so a `cd` there is gone after it, and one behind `&&` or `||` may not run.
    """

    def __init__(self, scope):
        self.words = []
        self.scope = scope
        self.piped = False
        self.backgrounded = False
        self.joined = None
        # The separator that ends it, and whether it sits in a compound command that may not run it.
        self.ended = None
        self.guarded = False
        # Whether it follows a `()`, as a function's body does: `f() { … }` defines it and runs nothing.
        self.defines = False
        # Whether `judge` followed this command as a `cd`, `pushd` or `popd` to a placed directory, so the command
        # an `&&` joins to it runs only where it moved.
        self.moved = False
        # What a heredoc or a here-string feeds it, which a shell reading its input runs as a command line.
        self.stdin = None


def segments(command):
    """The command's simple commands as `Segment`s, or `None` where `shlex` cannot read it."""
    text, bodies = prepared(command)
    lexer = shlex.shlex(text, posix=True, punctuation_chars=PUNCTUATION)
    lexer.whitespace = " \t\r"
    lexer.whitespace_split = True
    lexer.commenters = ""
    try:
        tokens = list(lexer)
    except ValueError:
        return None
    opened = 0
    scope = ()
    found = [Segment(scope)]
    redirection = None
    # A `()`, or a `(` and `)` with nothing between, is a function's name ending: the next command to take a word
    # is its body, past any newline.
    defining, previous = False, None
    for token, previous in zip(tokens, [previous, *tokens]):
        current = found[-1]
        if redirection is not None:
            # The word after a redirection is its file, a heredoc's body or a here-string's text.
            if redirection == "<<" and token.startswith(BODY) and token[len(BODY):].isdigit():
                current.stdin = bodies[int(token[len(BODY):])]
            elif redirection == "<<<":
                current.stdin = token
            redirection = None
            continue
        if not token or not set(token) <= set(PUNCTUATION):
            if defining and not current.words:
                current.defines = True
            defining = False
            current.words.append(token)
            continue
        if "()" in token or token.startswith(")") and previous is not None and previous.endswith("("):
            defining = True
        # A run can join a group's paren to a redirection, as `)>` does, so the parens are taken out of it first:
        # each opens or closes a subshell and ends the command before it.
        rest = token.replace("(", "").replace(")", "")
        for character in token:
            if character == "(":
                opened += 1
                scope = scope + (opened,)
            elif character == ")":
                scope = scope[:-1]
        if rest != token:
            found.append(Segment(scope))
        if "<" in rest or ">" in rest:
            # A bare number before a redirection is its descriptor.
            redirection = rest
            if current.words and current.words[-1].isdigit():
                current.words.pop()
        elif rest:
            if not rest.strip("\n") and not current.words:
                # A newline after `&&` or `||`, spaced or behind a comment, continues the list it joins.
                continue
            following = Segment(scope)
            if "|" in rest.replace("||", ""):
                current.piped = following.piped = True
            current.ended = rest
            if rest.strip("\n") in ("&&", "||"):
                following.joined = rest.strip("\n")
            found.append(following)
    return lists([segment for segment in found if segment.words])


# The reserved words that open and close a compound command, read as a segment's first word.
OPENERS = {"{", "if", "while", "until", "for", "select", "case"}
CLOSERS = {"}", "fi", "done", "esac"}


def lists(found):
    """`found`, each marked backgrounded where a lone `&` ends its list, and guarded where a compound may skip it.

    A lone `&` runs the whole and-or list before it in a subshell, a `{ … }` or a clause closed inside it included.
    Every command in an `if`, a loop or a `case`, and in a group behind `&&` or `||`, may not run.
    """
    frames = []
    start, guarded, carried = 0, False, False
    for position, segment in enumerate(found):
        # A compound may open behind another reserved word, as `then if` or `! {` does, or behind `time` and its
        # options. A function's body runs only when it is called, so it is guarded as a clause is.
        defining, timing, named, carried = segment.defines or carried, False, False, False
        for word in segment.words:
            if named:
                named = False
            elif word == "function":
                defining = named = True
            elif word in OPENERS:
                frames.append((start, guarded))
                guarded = guarded or defining or word != "{" or segment.joined is not None
                defining = False
            elif word == "time" or timing and word.startswith("-"):
                timing = True
            elif word not in PREFIXES:
                break
        else:
            # A line that ends at a definition's name, as `function NAME` alone, opens its body on the next. One whose
            # body is a command of another kind, as `f() ( true )`, has had its body, and carries nothing.
            carried = defining and "\n" in (segment.ended or "")
        segment.guarded = guarded
        if segment.words[0] in CLOSERS and frames:
            start, guarded = frames.pop()
        # `&&`, `|&` and a `case` clause's `;&` or `;;&` background nothing, and a list goes on past a newline after
        # `&&`, `||` or a pipe.
        ended = segment.ended or ""
        lone = "&" in ended.replace("&&", "").replace("|&", "").replace(";;&", "").replace(";&", "")
        if lone:
            for earlier in found[start:position + 1]:
                earlier.backgrounded = True
        if lone or ";" in ended or "\n" in ended and ended.strip("\n") not in ("&&", "||", "|", "|&"):
            start = position + 1
    return found


def option_width(option, valued):
    """The words a wrapper's option spans: two where it, or the last letter of its short cluster, takes a value."""
    if option in valued:
        return 2
    if option.startswith("--"):
        # getopt_long takes a prefix only one long name has as that name; `--name=value` is one word.
        return 2 if len(option) > 2 and "=" not in option and any(
            name.startswith(option) for name in valued if name.startswith("--")) else 1
    for position, letter in enumerate(option[1:], 1):
        if f"-{letter}" in valued:
            # A value letter before the cluster's end takes the rest of the word as its value.
            return 2 if position == len(option) - 1 else 1
    return 1


def command_word(words):
    """The index of the word this simple command runs, past assignments, prefix words and wrappers' options."""
    index = 0
    while index < len(words):
        word = words[index]
        if ASSIGNMENT.match(word) or word in PREFIXES:
            index += 1
        elif word in WRAPPERS:
            index += 1
            while index < len(words) and words[index].startswith("-") and words[index] != "-":
                option = words[index]
                index += option_width(option, WRAPPERS[word])
                if option == "--":
                    break
            # `timeout` takes its duration before the command.
            index += 1 if word == "timeout" else 0
        else:
            break
    return index


def echoed(segment):
    """What a stage writes into the pipe after it, as far as its text says; `None` where the text does not say.

    An `echo` or `printf` writes its words, and a `cat` with no file writes the heredoc or here-string it reads.
    """
    words = segment.words
    start = command_word(words)
    if start >= len(words):
        return None
    if words[start] == "cat" and all(word.startswith("-") and word != "-" for word in words[start + 1:]):
        return segment.stdin
    if words[start] not in ("echo", "printf"):
        return None
    rest = words[start + 1:]
    while words[start] == "echo" and rest and re.fullmatch(r"-[neE]+", rest[0]):
        rest = rest[1:]
    return " ".join(rest)


def shell_line(arguments, stdin):
    """The command line a shell with these arguments runs: its `-c` line, or its input where it reads that.

    As bash reads them: the long options, `-login` or `--login`, then the clusters, each `o` or `O` taking a word; the
    first operand after them is the `-c` line, or a script, which reads its input as data unless `-s` was given."""
    index = 0
    while index < len(arguments) and arguments[index].startswith("-"):
        name = arguments[index][2 if arguments[index].startswith("--") else 1:]
        if name not in SHELL_LONG and name not in SHELL_VALUED:
            break
        index += 2 if name in SHELL_VALUED else 1
    command = reads_input = False
    while index < len(arguments):
        argument = arguments[index]
        if argument in ("--", "-"):
            index += 1
            break
        if not argument.startswith(("-", "+")):
            break
        index += 1
        if not argument.startswith("--"):
            letters = argument[1:]
            command = command or "c" in letters
            reads_input = reads_input or "s" in letters
            index += letters.count("o") + letters.count("O")
    if index < len(arguments):
        return arguments[index] if command else (stdin if reads_input else None)
    return None if command else stdin


def long_option(name):
    """The long option `name` spells, or `None` for one GNU sed would reject."""
    if name in LONG_OPTIONS:
        return name
    matches = [option for option in LONG_OPTIONS if option.startswith(name)]
    return matches[0] if len(matches) == 1 else None


def in_place_files(arguments):
    """The files a `sed` with these arguments rewrites in place without `--binary`; empty where it rewrites none."""
    in_place = binary = script_given = only_operands = False
    operands = []
    index = 0
    while index < len(arguments):
        argument = arguments[index]
        index += 1
        if only_operands or argument == "-" or not argument.startswith("-"):
            operands.append(argument)
            continue
        if argument == "--":
            only_operands = True
            continue
        if argument.startswith("--"):
            name, equals, _ = argument[2:].partition("=")
            option = long_option(name)
            if option is None or option in ("help", "version"):
                return []
            in_place = in_place or option == "in-place"
            binary = binary or option == "binary"
            script_given = script_given or option in ("expression", "file")
            if option in LONG_WITH_ARGUMENT and not equals:
                index += 1
            continue
        for position, letter in enumerate(argument[1:], start=1):
            if letter == "i":
                in_place = True
                break
            if letter in SHORT_WITH_ARGUMENT:
                script_given = script_given or letter != "l"
                if position == len(argument) - 1:
                    index += 1
                break
            if letter not in SHORT_FLAGS:
                return []
            binary = binary or letter == "b"
    if not in_place or binary:
        return []
    return operands if script_given else operands[1:]


# The event's `cwd`, an own place beside the hook's and the project; `offence` sets it.
SESSION_CWD = None


def elsewhere(path):
    """Whether `path` is on a share the session does not stand on, which this hook must not open."""
    return GRAMMAR.foreign(path, SESSION_CWD, os.getcwd(), os.environ.get("CLAUDE_PROJECT_DIR"))


def pointed_elsewhere(directory):
    """Whether the checkout holding `directory` names its git directory on such a share, which git would open."""
    current = directory
    while not os.path.isdir(os.path.join(current, ".git")):
        marker = os.path.join(current, ".git")
        if os.path.isfile(marker):
            try:
                with open(marker, encoding="utf-8") as handle:
                    text = handle.read().strip()
            except (OSError, ValueError):
                return True
            pointer = text[len("gitdir:"):].strip()
            # As spelled too: a join puts the cwd drive before `\??\`, and git reads it unjoined.
            return text.startswith("gitdir:") and (elsewhere(pointer) or elsewhere(os.path.join(current, pointer)))
        parent = os.path.dirname(current)
        if parent == current:
            return False
        current = parent
    return False


def native(path):
    """`path` as this platform's Python opens it: Git Bash's `/c/...` is `C:/...` on Windows."""
    if os.name == "nt":
        path = MSYS_DRIVE.sub(lambda match: match.group(1) + ":", path)
    return os.path.expanduser(path)


def closing_brace(word, opening):
    """Where the brace at `opening` closes, and whether a comma sits directly inside it; `None` where none does."""
    depth = 0
    comma = False
    for position in range(opening, len(word)):
        character = word[position]
        if character == "{":
            depth += 1
        elif character == "}":
            depth -= 1
            if depth == 0:
                return position, comma
        elif character == "," and depth == 1:
            comma = True
    return None, False


def sequence(inner):
    """The words bash's `{x..y[..step]}` makes of `inner`, made lazily; `None` where it is not a sequence."""
    match = SEQUENCE.fullmatch(inner)
    if match is None:
        return None
    first, last, step = match.groups()
    if first.isalpha() != last.isalpha():
        return None
    # bash takes the step's size and walks towards the last end, and a step of 0 is 1.
    step = abs(int(step or 1)) or 1
    if first.isalpha():
        start, end = ord(first), ord(last)
        spell = chr
    else:
        start, end = int(first), int(last)
        # A leading zero on either end pads every word to the wider end's length.
        padded = any(re.match(r"-?0\d", bound) for bound in (first, last))
        width = max(len(first), len(last)) if padded else 0
        spell = lambda number: f"{number:0{width}d}"
    if end < start:
        step = -step
    return (spell(number) for number in range(start, end + (1 if step > 0 else -1), step))


def braces(word):
    """The words bash's brace expansion makes of `word`, in bash's order, made lazily so a long sequence costs only
    the words read. A brace with neither a comma directly inside it nor a sequence is text, as an unmatched one is,
    and bash looks on for one after it."""
    start = 0
    while True:
        opening = word.find("{", start)
        if opening < 0:
            yield word
            return
        closing, comma = closing_brace(word, opening)
        if closing is None:
            start = opening + 1
            continue
        inner = word[opening + 1:closing]
        if comma:
            alternatives = (expanded for part in split_commas(inner) for expanded in braces(part))
        else:
            alternatives = sequence(inner)
            if alternatives is None:
                start = opening + 1
                continue
        prefix, suffix = word[:opening], word[closing + 1:]
        for alternative in alternatives:
            for rest in braces(suffix):
                yield prefix + alternative + rest
        return


def split_commas(inner):
    """`inner` cut at each comma not inside a nested brace."""
    parts = []
    depth = 0
    current = []
    for character in inner:
        if character == "," and depth == 0:
            parts.append("".join(current))
            current = []
            continue
        if character == "{":
            depth += 1
        elif character == "}" and depth:
            depth -= 1
        current.append(character)
    parts.append("".join(current))
    return parts


def candidates(operand, cwd):
    """The regular files `operand` names from `cwd`, brace-expanded and globbed as bash would; empty for one this
    hook cannot place.

    A brace list's literal name is read as well, since the hook cannot tell a quoted list, which bash passes as it
    is, from a bare one: `docs/harness-boundaries.md`, *The sed line-ending guard*."""
    if "$" in operand or "`" in operand:
        return []
    words = list(itertools.islice(braces(operand), MAX_FILES + 1))
    if words != [operand]:
        words.append(operand)
    found = []
    walked = 0
    for word in words:
        path = native(word)
        if elsewhere(path):
            continue
        if not os.path.isabs(path):
            if cwd is None:
                continue
            path = os.path.join(cwd, path)
        matches = []
        if any(character in word for character in "*?["):
            # `iglob` walks lazily, so the bound stops the walk itself and not only the reads after it; it is one
            # bound for the operand, however many words its braces make.
            matches = list(itertools.islice(glob.iglob(path), MAX_FILES + 1 - walked))
            walked += len(matches)
        for match in matches or [path]:
            if match not in found and os.path.isfile(match):
                found.append(match)
        if walked > MAX_FILES or len(found) > MAX_FILES:
            break
    return found


def holds_crlf(path):
    """Whether the file's first `MAX_READ` bytes hold a CRLF; `False` for one that cannot be read."""
    try:
        with open(path, "rb") as handle:
            return b"\r\n" in handle.read(MAX_READ)
    except OSError:
        return False


def resolved(path):
    return os.path.normcase(os.path.realpath(path))


def inside(path, tree):
    return path == tree or path.startswith(os.path.join(tree, ""))


def git_on_path(cwd):
    """git found on PATH alone, as a full path, or None.

    Absolute PATH entries, nothing at or below where the session stands, and on Windows only `.exe` or `.com`:
    `docs/harness-boundaries.md`, *The sed line-ending guard*."""
    fenced = [resolved(tree) for tree in (os.getcwd(), cwd, os.environ.get("CLAUDE_PROJECT_DIR")) if tree]
    names = ("git.exe", "git.com") if os.name == "nt" else ("git",)
    for entry in os.environ.get("PATH", "").split(os.pathsep):
        directory = entry.strip('"')
        # `Path`, not `os.path.isabs`: before Python 3.13 that calls `\tools`, which has no drive, absolute on Windows.
        if not Path(directory).is_absolute() or any(inside(resolved(directory), tree) for tree in fenced):
            continue
        for name in names:
            path = os.path.join(directory, name)
            if not (os.path.isfile(path) and os.access(path, os.X_OK)):
                continue
            if not any(inside(resolved(path), tree) for tree in fenced):
                return path
    return None


def tracked(path, git):
    """Whether `path` is in its checkout's index; `False` wherever git cannot say.

    `core.fsmonitor` is switched off because `ls-files` would otherwise run the command a foreign checkout's config
    names, and the path is the session's to choose; `GIT_OPTIONAL_LOCKS=0` keeps the read from refreshing the index.
    """
    directory, name = os.path.split(os.path.abspath(path))
    if pointed_elsewhere(directory):
        return False
    try:
        result = subprocess.run(
            # `--literal-pathspecs`, or an untracked `x[ab].txt` would match a tracked `xa.txt`.
            [git, "--literal-pathspecs", "-c", "core.fsmonitor=false", "-C", directory,
             "ls-files", "--error-unmatch", "--", name],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            env={**os.environ, "GIT_OPTIONAL_LOCKS": "0"}, timeout=GIT_TIMEOUT,
        )
    except (OSError, subprocess.SubprocessError):
        return False
    return result.returncode == 0


def offence(event, strips_cr=os.name == "nt"):
    """The reason to refuse this call, or `None` to let it through.

    `strips_cr` is whether this platform's sed drops the CR, which only Git Bash's does: GNU sed on Linux and macOS
    keeps it as an ordinary character, so refusing there would block an edit that is harmless.
    """
    if GRAMMAR is None:
        raise RuntimeError(f"path_grammar.py did not load:\n{GRAMMAR_FAILURE}")
    if not strips_cr or event.get("tool_name") != "Bash":
        return None
    tool_input = event.get("tool_input")
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if not isinstance(command, str) or "sed" not in command:
        return None
    global SESSION_CWD
    cwd = event.get("cwd")
    cwd = cwd if isinstance(cwd, str) and cwd else os.getcwd()
    SESSION_CWD = cwd
    return judge(command, cwd, git_on_path(cwd), [0], 0)


def moved_to(directory, cwd):
    """Where a plain `cd directory` from `cwd` lands, or `None` where that cannot be placed.

    Bash refuses a directory that is not there and stays put, and a failed `pushd` pushes nothing; one an earlier
    command on the line makes is not there yet either. Either way the move is not followed.
    """
    target = native(directory)
    if not (os.path.isabs(target) or elsewhere(target)):
        target = os.path.join(cwd, target) if cwd else None
    if target is None or elsewhere(target) or not os.path.isdir(target):
        return None
    return target


def env_directories(prefix):
    """The directories each `env -C` or `--chdir` among the wrapper words `prefix` names, in order; `None` for one
    holding an expansion."""
    found, position = [], 0
    for index, word in enumerate(prefix):
        # A word an earlier `env` took as an option's value is no `env` of its own.
        if word != "env" or index < position:
            continue
        position = index + 1
        while position < len(prefix) and prefix[position].startswith("-") and prefix[position] not in ("-", "--"):
            option = prefix[position]
            position += 1
            if option.startswith("--"):
                name, given, value = option[2:].partition("=")
                if not (name and "chdir".startswith(name)):
                    position += 0 if given or option_width(option, WRAPPERS["env"]) == 1 else 1
                    continue
                if not given:
                    value = prefix[position] if position < len(prefix) else None
                    position += 1
            else:
                at = next((at for at, letter in enumerate(option[1:], 1) if f"-{letter}" in WRAPPERS["env"]), None)
                if at is None:
                    continue
                value = option[at + 1:]
                if not value:
                    value = prefix[position] if position < len(prefix) else None
                    position += 1
                if option[at] != "C":
                    continue
            found.append(None if value is None or "$" in value or "`" in value else value)
    return found


def places(found):
    """`found` without repeats, in order, and at most `MAX_PLACES` of them."""
    return tuple(dict.fromkeys(found))[:MAX_PLACES]


def judge(command, cwd, git, checked, depth):
    """The refusal for one command line run from `cwd`, or `None`; `checked` counts the files read across calls."""
    found = segments(command)
    if found is None:
        return None
    # The directories each subshell may stand in, `None` among them for one that cannot be placed. One opened here
    # starts where its parent stands when it is first seen, which is where bash forks it, since the commands are read
    # in order.
    directories = {(): (cwd,)}
    # Each subshell's `pushd` stack, inherited as its directories are; `None` once it is not followed.
    stacks = {(): []}
    for position, segment in enumerate(found):
        previous = found[position - 1] if position else None
        scope = segment.scope
        if scope not in directories:
            parent = next(scope[:length] for length in range(len(scope) - 1, -1, -1)
                          if scope[:length] in directories)
            directories[scope] = directories[parent]
            stacks[scope] = None if stacks[parent] is None else list(stacks[parent])
        here = directories[scope]
        words = segment.words
        start = command_word(words)
        if start >= len(words):
            continue
        name = words[start]
        arguments = words[start + 1:]
        if name in ("cd", "pushd", "popd"):
            # A pipeline stage's or a backgrounded `cd` moves nothing after it. Only a plain `cd <dir>` or
            # `pushd <dir>`, and a bare `popd` back to a placed entry, can be followed; any other form leaves the files
            # after it unplaceable.
            if segment.piped or segment.backgrounded:
                continue
            # One that may not run leaves the commands after it where they were as well as where it moves them: in a
            # compound that may skip it, behind `||`, or behind `&&` unless what it joins is a move already followed.
            # A loop's body is followed for one pass.
            conditional = segment.guarded or segment.joined == "||" or segment.joined == "&&" and not (
                previous is not None and previous.moved and previous.scope == scope)
            if name == "popd":
                stack = stacks[scope]
                moved = stack.pop() if not arguments and stack else (None,)
                if arguments or conditional:
                    stacks[scope] = None
            else:
                plain = not (len(arguments) != 1 or "$" in arguments[0] or arguments[0] == "-" or (
                    name == "pushd" and arguments[0][:1] in ("+", "-")))
                moved = tuple(moved_to(arguments[0], place) if plain else None for place in here)
                plain = plain and None not in moved
                if name == "pushd":
                    # Any other `pushd` form, `-n <dir>` among them, changes the stack in ways not followed here, and
                    # one that may not run leaves it unknown, so every `popd` after either is unplaceable too.
                    if plain and not conditional and stacks[scope] is not None:
                        stacks[scope].append(here)
                    else:
                        stacks[scope] = None
            segment.moved = None not in moved and not conditional
            directories[scope] = places(here + moved if conditional else moved)
            continue
        # `env -C DIR` runs the command after it from DIR, so its files are looked for there.
        for directory in env_directories(words[:start]):
            here = tuple(None if place is None or directory is None else moved_to(directory, place)
                         for place in here)
        program = os.path.basename(native(name)).lower()
        if program in SHELLS:
            # The line runs from here: a `-c` line, whose `-c` may share a cluster as in `-lc`, or the shell's input.
            stdin = segment.stdin
            if stdin is None and segment.piped and previous is not None and previous.piped:
                stdin = echoed(previous)
            line = shell_line(arguments, stdin)
            if line and depth < MAX_DEPTH:
                for place in here:
                    reason = judge(line, place, git, checked, depth + 1)
                    if reason:
                        return reason
            continue
        if program not in ("sed", "sed.exe"):
            continue
        for cwd, operand in ((place, operand) for operand in in_place_files(arguments) for place in here):
            for path in candidates(operand, cwd):
                checked[0] += 1
                if checked[0] > MAX_FILES:
                    return None
                if holds_crlf(path) and git is not None and tracked(path, git):
                    # A glob's refusal names the file it matched, since the pattern is not what is tracked.
                    spelled = path == os.path.join(cwd or "", native(operand))
                    named = operand if spelled else f"{path}, which {operand} matches,"
                    return (
                        f"guard-sed-crlf: `sed -i` would rewrite {named} with LF endings. It is tracked and holds "
                        "CRLF line endings, and Git Bash's sed drops the CR from every line it writes in place, so "
                        "the file comes back LF: a whitespace or editorconfig check fails on lines nobody edited, "
                        "and where the index holds CRLF the diff shows every line. "
                        f"{SAFE_FORMS}"
                    )
    return None


def deny(reason):
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
    # An event that is not UTF-8 holds a command nothing read, so it is refused, as the argv guards refuse one.
    try:
        text = sys.stdin.buffer.read().decode("utf-8")
    except UnicodeDecodeError:
        return deny("guard-sed-crlf: this hook event is not UTF-8, so the command in it cannot be read; refusing "
                    "rather than admitting what nothing judged.")
    try:
        event = json.loads(text)
    except (json.JSONDecodeError, ValueError):
        # As in guard-edit-target.py: a hook that cannot read its own input has established nothing, and refusing
        # every command on a malformed event would turn a defect here into a dead session.
        print("guard-sed-crlf: unreadable hook event; not judging", file=sys.stderr)
        return 0
    if not isinstance(event, dict):
        print("guard-sed-crlf: hook event is not an object; not judging", file=sys.stderr)
        return 0

    # A crash here would be empty stdout, which PreToolUse reads as admitted: the argv guards' fail-open, refused as
    # they refuse it (`docs/harness-boundaries.md`, *The sed line-ending guard*).
    try:
        reason = offence(event)
    except Exception:  # noqa: BLE001
        traceback.print_exc(file=sys.stderr)
        reason = ("guard-sed-crlf: this command crashed the sed line-ending guard, so nothing about it is "
                  "established; refusing rather than admitting what nothing judged. The traceback is on stderr.")
    if reason is None:
        return 0
    return deny(reason)


if __name__ == "__main__":
    sys.exit(main())
