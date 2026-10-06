#!/usr/bin/env python3
"""Refuse a session's first search for a named symbol once, with the index
lookup that answers it as the reason, while the session has asked the index
nothing; a call it cannot judge goes through, and it returns 0 whatever
happens."""

from __future__ import annotations

import json
import os
import re
import shlex
import sys
import tempfile
from pathlib import Path

CBX = "bash .claude/skills/codebase-index/scripts/cbx"
MCP = "mcp__codebase-index__"

# Each `cbx` subcommand a hint can name and the MCP tool answering the same
# question; `test_index_query_hint.py` holds the prompt hint's routes to it.
TOOLS = {
    "search": "search_code",
    "explain": "explain_code",
    "symbol": "find_symbol",
    "refs": "find_refs",
    "impact": "impact_of",
}

# One refusal per session, recorded where each of its calls can see it; a
# call that cannot record it goes through, so none is refused twice.
ASKED = "claude-index-ask"
SESSION = re.compile(r"^[A-Za-z0-9_-]{1,128}$")

# The most names one refusal spells out.
NAMES = 3

# The subcommands and tools that answer a question; stats, health and upkeep
# read the index without asking it anything.
ASKING = ("search", "explain", "architecture", "symbol", "refs", "impact",
          "diff-impact", "path", "describe", "verify")
UPKEEP = {"healthcheck", "index_stats"}
CLIS = {"cbx", "cbx.ps1", "codebase-index", "codebase_index"}

# The tools that run a command line, and the escape character the split
# reads for each: none for PowerShell, whose backslash is a path and whose
# backtick only joins a continued line here.
SHELLS = {"Bash": "\\", "PowerShell": ""}

# The programs that run the script or module they are handed, and each one's
# options that consume the next word; PowerShell's are read in any case.
POWERSHELLS = {"pwsh", "powershell"}
PYTHONS = {"py", "python", "python3"}
INTERPRETERS = {
    "bash": set(), "sh": set(),
    **dict.fromkeys(POWERSHELLS, {"-executionpolicy", "-ep", "-workingdirectory", "-wd"}),
    **dict.fromkeys(PYTHONS, {"-X", "-W"}),
}

SEPARATORS = set("|&;()\n")
REDIRECTS = set("<>")

# A heredoc's opening, `<<TAG`, `<<-TAG` or a quoted tag; not `<<<`.
HEREDOC = re.compile(r"(?<!<)<<(-?)[ \t]*(['\"]?)([A-Za-z_][\w.-]*)\2")

# The options of grep, rg and git grep that consume the next word.
VALUED = {"-e", "-f", "-A", "-B", "-C", "-m", "-g", "-t", "-T", "-M",
          "--glob", "--iglob", "--type", "--type-not", "--max-count", "--max-depth",
          "--include", "--exclude", "--exclude-dir",
          "--regexp", "--file", "--context", "--before-context", "--after-context"}

# The prose half of the tree is a grep question: which chapter owns a rule is
# not one the index answers, as the skill says.
PROSE = re.compile(r"(?:^|[\\/])docs(?:[\\/]|$)|\.md$|^md$|^\*\.md$|^markdown$", re.IGNORECASE)

EXCLUDES = ("!", ":!", ":^", ":(exclude)")

# The options naming which files a search reads, in any of the three tools.
FILTERS = {"-g", "--glob", "--iglob", "-t", "--type", "--include"}

IDENTIFIER = re.compile(r"^[^\W\d]\w*(?:\.[^\W\d]\w*)*$")


def unheredoc(command: str) -> str:
    """`command` without its heredoc bodies, which are data, not commands."""
    kept, waiting = [], []
    for line in command.splitlines():
        if waiting:
            dash, tag = waiting[0]
            if (line.lstrip("\t") if dash else line) == tag:
                waiting.pop(0)
            continue
        kept.append(line)
        waiting = [(found.group(1), found.group(3)) for found in HEREDOC.finditer(line)]
    return "\n".join(kept)


def tokens(command: str, escape: str) -> list[str]:
    """The words and operators of `command`, a newline outside quotes being
    an operator; a line at a time when the whole will not split."""
    text = unheredoc(command.replace("\\\n", " ") if escape else re.sub(r"`\r?\n", " ", command))
    try:
        lexer = shlex.shlex(text, posix=True, punctuation_chars="|&;<>()\n")
        lexer.whitespace, lexer.commenters, lexer.escape = " \t\r", "", escape
        lexer.whitespace_split = True
        return list(lexer)
    except ValueError:
        return [word for line in text.splitlines() for word in [*line.split(), "\n"]]


def segments(command: str, escape: str = "\\"):
    """Each simple command in `command` as (argv, piped, after): quotes
    honoured, redirections and their targets dropped, `piped` when it reads
    standard input, from the one before it or a redirection, and `after` the
    operator ending it."""
    words = tokens(command, escape)
    argv, piped, skip = [], False, False
    for index, token in enumerate(words):
        following = words[index + 1] if index + 1 < len(words) else ""
        if skip:
            skip = False
        elif token and set(token) <= SEPARATORS:
            yield argv, piped, token
            argv, piped = [], token.strip("\n") in ("|", "|&")
        elif token and set(token) <= SEPARATORS | REDIRECTS:
            skip, piped = True, piped or set(token) == {"<"}
        elif not (token.isdigit() and following and set(following) & REDIRECTS
                  and set(following) <= SEPARATORS | REDIRECTS):
            argv.append(token)
    yield argv, piped, ""


def prose_only(paths: list[str], filters: list[str]) -> bool:
    """True when the search reads only prose: its file filters, where it
    names any, or else the paths it names; an rg `!` glob or a git exclude
    pathspec leaves prose out rather than reading it."""
    named = filters or paths
    return bool(named) and all(not item.startswith(EXCLUDES) and PROSE.search(item.strip("'\"")) for item in named)


def located(cwd: str, path: str) -> Path:
    """`path` as the shell that ran it would find it, Git Bash's `/c/…`
    drive form included."""
    text = os.path.expanduser(path.strip("'\""))
    drive = re.match(r"^/([A-Za-z])(?=/|$)", text)
    if sys.platform == "win32" and drive:
        text = f"{drive.group(1)}:/{text[3:]}"
    return Path(cwd or ".", text)


def files_only(paths: list[str], cwd: str) -> bool:
    """True when every path the search names is one file, which a Read
    answers as well as the index would."""
    try:
        return bool(paths) and all(located(cwd, path).is_file() for path in paths)
    except (OSError, ValueError):
        return False


def checkout(cwd: str) -> Path | None:
    """The checkout `cwd` sits in, by the `.git` at or above it."""
    try:
        start = Path(cwd or ".").resolve()
        return next((place for place in (start, *start.parents) if (place / ".git").exists()), None)
    except OSError:
        return None


def elsewhere(paths: list[str], base: str, cwd: str) -> bool:
    """True when the search reads outside the checkout `cwd` sits in, whose
    index is the only one a hint can name."""
    root = checkout(cwd)
    try:
        return root is not None and any(
            not located(base, path).resolve().is_relative_to(root) for path in paths or ["."])
    except (OSError, ValueError):
        return False


def shown(paths: list[str], base: str, cwd: str) -> list[str]:
    """The paths a search reads, `.` when it names none, each as it stands
    in the checkout `cwd` sits in, so prose reads the same from any directory."""
    root = checkout(cwd)
    named = []
    for path in paths or ["."]:
        if path.startswith(EXCLUDES):
            named.append(path)
            continue
        try:
            found = located(base, path).resolve()
            named.append(found.relative_to(root).as_posix() if root and found.is_relative_to(root) else path)
        except (OSError, ValueError):
            named.append(path)
    return named


def shell_search(command: str, cwd: str, escape: str = "\\") -> str | None:
    """The pattern of a tree-wide grep, rg or git grep in `command`, or None.
    A grep with no recursion reads one file or a pipe, which the index does
    not replace, and neither does an rg filtering a pipe."""
    shell, outer, before = cwd, [], ""
    for argv, piped, after in segments(command, escape):
        # A subshell's `cd` ends with it.
        for mark in before:
            if mark == "(":
                outer.append(shell)
            elif mark == ")" and outer:
                shell = outer.pop()
        before = after
        while argv and re.match(r"^[A-Za-z_]\w*=", argv[0]):
            argv = argv[1:]
        if not argv:
            continue
        program = executable(argv[0])
        if program == "cd":
            shell = str(located(shell, argv[1] if len(argv) > 1 else "~"))
            continue
        base = shell
        if program == "git" and "grep" in argv[1:4]:
            if argv[1] == "-C" and argv[2] != "grep":
                base = str(located(shell, argv[2]))
            argv, tree = argv[argv.index("grep") + 1:], True
        elif program == "rg" and "--files" not in argv:
            argv, tree = argv[1:], not piped
        elif program in ("grep", "egrep", "fgrep"):
            argv, tree = argv[1:], False
        else:
            continue
        pattern, paths, filters, recursive = None, [], [], False
        index = 0
        while index < len(argv):
            word = argv[index]
            if word in VALUED and index + 1 < len(argv):
                value = argv[index + 1]
                if word in ("-e", "--regexp") and pattern is None:
                    pattern = value
                elif word in FILTERS:
                    filters.append(value)
                index += 2
                continue
            if word.split("=", 1)[0] in FILTERS and "=" in word:
                filters.append(word.split("=", 1)[1])
            elif program == "rg" and re.match(r"^-[gt][^-]", word):
                filters.append(word[2:])
            elif word in ("--recursive", "--dereference-recursive") or re.match(r"^-[A-Za-z]*[rR]", word):
                recursive = True
            elif not word.startswith("-") or word == "-":
                if pattern is None:
                    pattern = word
                else:
                    paths.append(word)
            index += 1
        if program == "rg" and paths:
            tree = True
        if (tree or recursive) and pattern and not prose_only(shown(paths, base, cwd), filters) \
                and not files_only(paths, base) and not elsewhere(paths, base, cwd):
            return pattern
    return None


def search(name: str, given: dict, cwd: str) -> str | None:
    """The pattern when this tool call is a code search, else None."""
    if name == "Grep":
        pattern = given.get("pattern")
        if not isinstance(pattern, str) or not pattern:
            return None
        paths = [given["path"]] if isinstance(given.get("path"), str) else []
        filters = [given[key] for key in ("glob", "type") if isinstance(given.get(key), str)]
        if prose_only(shown(paths, cwd, cwd), filters) or files_only(paths, cwd) or elsewhere(paths, cwd, cwd):
            return None
        return pattern
    if name in SHELLS and isinstance(given.get("command"), str):
        return shell_search(given["command"], cwd, SHELLS[name])
    return None


def executable(word: str) -> str:
    return word.replace("\\", "/").rsplit("/", 1)[-1].removesuffix(".exe")


def runs_cli(argv: list[str], depth: int = 0) -> bool:
    """True when `argv` asks the CLI a question: directly, through an
    interpreter and its options, or in the command a shell's `-c` runs, so
    `-m codebase_index` reads as the CLI itself."""
    while argv and re.match(r"^[A-Za-z_]\w*=", argv[0]):
        argv = argv[1:]
    program = executable(argv[0]).lower() if argv else ""
    if program in INTERPRETERS:
        rest = argv[1:]
        while rest and rest[0].startswith("-"):
            option = rest[0].lower() if program in POWERSHELLS else rest[0]
            if option in ("-c", "-command"):
                escape = "" if program in POWERSHELLS else "\\"
                return (program not in PYTHONS and depth < 2 and len(rest) > 1 and any(
                    runs_cli(inner, depth + 1) for inner, _piped, _after in segments(rest[1], escape)))
            rest = rest[2:] if option in INTERPRETERS[program] else rest[1:]
            if program in POWERSHELLS and option in ("-f", "-file"):
                break
        argv = rest
    return len(argv) > 1 and executable(argv[0]) in CLIS and argv[1] in ASKING


def lookup(name: str, given: dict) -> bool:
    """True when the call asked the index: an MCP tool, or a command that
    runs the CLI, never one that merely names it."""
    if name.startswith(MCP):
        return name[len(MCP):] not in UPKEEP
    if name not in SHELLS or not isinstance(given.get("command"), str):
        return False
    return any(runs_cli(argv) for argv, _piped, _after in segments(given["command"], SHELLS[name]))


def calls(transcript: Path):
    """Every tool call the transcript records, as (name, input). Only the
    lines naming a tool call are parsed: a long session's transcript runs to
    tens of megabytes, and this runs before the agent's call does."""
    with transcript.open("rb") as handle:
        for line in handle:
            if b'"tool_use"' not in line:
                continue
            try:
                entry = json.loads(line)
            except ValueError:
                continue
            message = entry.get("message") if isinstance(entry, dict) else None
            content = message.get("content") if isinstance(message, dict) else None
            for block in content if isinstance(content, list) else ():
                if isinstance(block, dict) and block.get("type") == "tool_use":
                    given = block.get("input")
                    yield str(block.get("name")), given if isinstance(given, dict) else {}


def in_worktree(cwd: str) -> bool:
    """True when `cwd` is in a linked worktree: a `.git` file whose gitdir
    carries a `commondir`. The MCP server keeps the root it started in."""
    try:
        start = Path(cwd).resolve()
        for candidate in (start, *start.parents):
            marker = candidate / ".git"
            if marker.is_dir():
                return False
            if marker.is_file():
                text = marker.read_text(encoding="utf-8").strip()
                if not text.startswith("gitdir:"):
                    return False
                git_dir = (candidate / text[len("gitdir:"):].strip()).resolve()
                return (git_dir / "commondir").is_file()
    except (OSError, ValueError):
        return False
    return False


def names(pattern: str) -> list[str]:
    """The symbols a search pattern names: one, or an alternation of them,
    word boundaries, a call form and a wrapping group dropped. A name in one
    case with no underscore, `timeout` or `TODO`, or a file name, `CLAUDE.md`,
    is text, and text, a message or a pattern is grep's to list whole."""
    text = re.sub(r"\\b|\\<|\\>", "", pattern).strip()
    text = re.sub(r"^\\?\((.*?)\\?\)$", r"\1", text)
    found = []
    for part in re.split(r"\\?\|", text):
        part = re.sub(r"\s*(?:\\\(|\()$", "", part.strip())
        mixed = part != part.lower() and part != part.upper()
        extension = "." in part and part.rsplit(".", 1)[1].islower()
        if not (IDENTIFIER.match(part) and ("_" in part or mixed)) or extension:
            return []
        if part not in found:
            found.append(part)
    return found


def record(session: object) -> Path | None:
    """The file recording `session`'s refusal, or None for an id that
    cannot name one."""
    if not isinstance(session, str) or not SESSION.match(session):
        return None
    return Path(tempfile.gettempdir()) / ASKED / session


def first(asked: Path) -> bool:
    """True for the one call that creates `asked`, so a parallel batch is
    refused once."""
    try:
        asked.parent.mkdir(exist_ok=True)
        os.close(os.open(asked, os.O_CREAT | os.O_EXCL | os.O_WRONLY))
    except OSError:
        return False
    return True


def ask(found: list[str], worktree: bool) -> str:
    """The refusal's reason: one route to the index, the one that answers
    from this tree, because the MCP server serves the checkout it started in."""
    named = ", ".join(f"`{name}`" for name in found)
    if worktree:
        runs = ", ".join(f'`{CBX} refs "{name}" --json`' for name in found)
        route = (f"load the codebase-index skill, whose grant the CLI runs under, then run {runs}, one per "
                 f"call, and `{CBX} symbol \"{found[0]}\" --json` for where it is defined; the CLI reads this "
                 "worktree's own index")
    else:
        load = ",".join(MCP + TOOLS[tool] for tool in ("refs", "symbol"))
        route = (f"call `{MCP}{TOOLS['refs']}` for what uses each and `{MCP}{TOOLS['symbol']}` for where "
                 f"it is defined, loaded first with ToolSearch `select:{load}`")
    return (f"Before searching for {named}, ask the code index: {route}. This search is refused once "
            "per session; rerun it unchanged if you still need every occurrence.")


def answer(event: dict) -> str | None:
    """Why to refuse this call, or None to let it run. A subagent's profile
    may hold neither the MCP tools nor a shell, so it is never refused."""
    name, given = event.get("tool_name"), event.get("tool_input")
    if not isinstance(name, str) or not isinstance(given, dict) or event.get("agent_id"):
        return None
    cwd = event.get("cwd") if isinstance(event.get("cwd"), str) else ""
    pattern = search(name, given, cwd)
    found = names(pattern) if pattern else []
    transcript, asked = event.get("transcript_path"), record(event.get("session_id"))
    # A spent ask is read before the transcript, so it costs every later search no scan.
    if not found or not isinstance(transcript, str) or not transcript or asked is None or asked.exists():
        return None
    if any(lookup(called, called_with) for called, called_with in calls(Path(transcript))):
        return None
    if not first(asked):
        return None
    return ask(found[:NAMES], bool(cwd) and in_worktree(cwd))


def main() -> int:
    # Bytes, decoded here: Python reads a Windows pipe in the ANSI code page.
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
        found = answer(event) if isinstance(event, dict) else None
        if found:
            print(json.dumps({"hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": found}}))
    except Exception:  # noqa: BLE001 - a call it cannot judge goes through
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
