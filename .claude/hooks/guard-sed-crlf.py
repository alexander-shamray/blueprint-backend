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
from pathlib import Path

# What a refusal names as the forms that keep the file's endings. `--binary` is admitted: measured under Git Bash's
# GNU sed 4.9, `sed --binary -i` writes the CR back where a plain `-i` drops it.
SAFE_FORMS = (
    "Edit it with the Edit tool, or with a Python script that opens it with newline=\"\" and writes its own line "
    "ending back; `sed --binary -i` keeps the CR and is admitted."
)

# `shlex` returns a run of these as one token, `;\n` or `>&` alike, so a token is a redirection when it holds `<` or
# `>` and a separator between commands otherwise. A newline is one of them, so `shlex` is told it is not whitespace.
PUNCTUATION = ";&|()<>\n"

# Words that run the command after them, so `sed` behind one is still the command: the prefix commands, and the
# reserved words that open a group or a clause, as in `if …; then sed -i …; fi`.
PREFIXES = {
    "command", "exec", "time", "builtin", "nohup",
    "{", "!", "if", "then", "elif", "else", "while", "until", "do",
}

# Programs that run the command after their own options, each with the options that take a separate argument.
WRAPPERS = {
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
SHELL_COMMAND = re.compile(r"-[A-Za-z]*c[A-Za-z]*")
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
    """One simple command: its words, the subshells it runs in, and whether it is a stage of a pipeline.

    The last two say whether a `cd` in it moves the commands after it: bash runs a `( … )` group and every stage of
    a pipeline in a subshell of its own, so a `cd` there is gone once the group or the pipeline ends.
    """

    def __init__(self, scope):
        self.words = []
        self.scope = scope
        self.piped = False
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
    for token in tokens:
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
            current.words.append(token)
            continue
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
            following = Segment(scope)
            if "|" in rest.replace("||", ""):
                current.piped = following.piped = True
            found.append(following)
    return [segment for segment in found if segment.words]


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
                index += 2 if option in WRAPPERS[word] else 1
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
    """The command line a shell with these arguments runs: its `-c` line, or its input where it reads that."""
    index = 0
    reads_input = False
    while index < len(arguments):
        argument = arguments[index]
        if SHELL_COMMAND.fullmatch(argument):
            return arguments[index + 1] if index + 1 < len(arguments) else None
        if argument in ("-o", "+o"):
            index += 2
            continue
        if argument == "--" or not argument.startswith(("-", "+")):
            # An operand is a script, which reads its input as data, unless `-s` said to read commands from it.
            return stdin if reads_input else None
        reads_input = reads_input or (not argument.startswith("--") and "s" in argument)
        index += 1
    return stdin


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
    if not strips_cr or event.get("tool_name") != "Bash":
        return None
    tool_input = event.get("tool_input")
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if not isinstance(command, str) or "sed" not in command:
        return None
    cwd = event.get("cwd")
    cwd = cwd if isinstance(cwd, str) and cwd else os.getcwd()
    return judge(command, cwd, git_on_path(cwd), [0], 0)


def judge(command, cwd, git, checked, depth):
    """The refusal for one command line run from `cwd`, or `None`; `checked` counts the files read across calls."""
    found = segments(command)
    if found is None:
        return None
    # The directory each subshell stands in. One opened here starts where its parent stands when it is first seen,
    # which is where bash forks it, since the commands are read in order.
    directories = {(): cwd}
    for position, segment in enumerate(found):
        previous = found[position - 1] if position else None
        scope = segment.scope
        if scope not in directories:
            parent = next(scope[:length] for length in range(len(scope) - 1, -1, -1)
                          if scope[:length] in directories)
            directories[scope] = directories[parent]
        cwd = directories[scope]
        words = segment.words
        start = command_word(words)
        if start >= len(words):
            continue
        name = words[start]
        arguments = words[start + 1:]
        if name == "cd":
            # A pipeline stage's `cd` moves nothing after it. Only a plain `cd <dir>` can be followed; any other form
            # leaves the files after it unplaceable.
            if segment.piped:
                continue
            if len(arguments) != 1 or "$" in arguments[0] or arguments[0] == "-":
                directories[scope] = None
            else:
                target = native(arguments[0])
                directories[scope] = (target if os.path.isabs(target)
                                      else (os.path.join(cwd, target) if cwd else None))
            continue
        program = os.path.basename(native(name)).lower()
        if program in SHELLS:
            # The line runs from here: a `-c` line, whose `-c` may share a cluster as in `-lc`, or the shell's input.
            stdin = segment.stdin
            if stdin is None and segment.piped and previous is not None and previous.piped:
                stdin = echoed(previous)
            line = shell_line(arguments, stdin)
            if line and depth < MAX_DEPTH:
                reason = judge(line, cwd, git, checked, depth + 1)
                if reason:
                    return reason
            continue
        if program not in ("sed", "sed.exe"):
            continue
        for operand in in_place_files(arguments):
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


def main():
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except (json.JSONDecodeError, ValueError, UnicodeDecodeError):
        # As in guard-edit-target.py: a hook that cannot read its own input has established nothing, and refusing
        # every command on a malformed event would turn a defect here into a dead session.
        print("guard-sed-crlf: unreadable hook event; not judging", file=sys.stderr)
        return 0
    if not isinstance(event, dict):
        print("guard-sed-crlf: hook event is not an object; not judging", file=sys.stderr)
        return 0

    reason = offence(event)
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
