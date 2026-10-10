#!/usr/bin/env python3
"""Sum Claude Code's recorded token usage per slash command and agent type.

    python tools/token-usage/token_usage.py [PROJECT_DIR ...] [--since 2026-10-01] [--json]
"""

from __future__ import annotations

import argparse
import bisect
import glob
import io
import json
import posixpath
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

NO_COMMAND = "(no command)"
MAIN = "main"
SUBAGENT = "subagent"
COMMAND = re.compile(r"<command-name>/?([^<\s]+)</command-name>")
INJECTED = ("<bash-", "<local-command-", "[Request interrupted")
# Harness commands that act on the session, typed mid-run, after which the run they interrupted goes on.
CARRY_ON = {"/compact", "/autocompact", "/reload-plugins", "/context", "/cost", "/status", "/model", "/effort",
            "/fast", "/config", "/permissions", "/memory", "/mcp", "/hooks", "/agents", "/plugin", "/help"}
WORKTREE = re.compile(r"[\\/]\.claude[\\/]worktrees[\\/].*$")
# A shell command whose markdown arguments it prints: the reads that do not go through the Read tool.
SHELL_READERS = {"cat", "head", "tail", "sed", "less", "more", "type", "gc", "get-content"}
SHELL_SEGMENT = re.compile(r"&&|\|\||[;|\n]")
SHELL_WORD = re.compile(r'"[^"]*"|\'[^\']*\'|\S+')
# Where a run starts: what a command, an agent or a skill names, it is handed.
ENTRY = re.compile(r"\.claude/(?:commands/[^/]+|agents/[^/]+|skills/[^/]+/SKILL)\.md")
MENTION = re.compile(r"[\w./<>{}-]*[\w>}]\.md\b")
PLACEHOLDER = re.compile(r"<[^<>/]+>|\{[^{}/]+\}")
LINKS = ("entry point", "named by entry", "CLAUDE.md only", "none")

# Anthropic's prompt-caching prices as multiples of the base input price.
WRITE_5M = 1.25
WRITE_1H = 2.0
READ = 0.1


@dataclass
class Usage:
    calls: int = 0
    input: int = 0
    write_5m: int = 0
    write_1h: int = 0
    read: int = 0
    output: int = 0
    woken: float = 0.0
    contexts: set[str] = field(default_factory=set)

    def add(self, usage: dict, context: str, woken: bool = False) -> None:
        before = self.equivalent
        self.calls += 1
        self.input += usage.get("input_tokens") or 0
        self.read += usage.get("cache_read_input_tokens") or 0
        self.output += usage.get("output_tokens") or 0
        split = usage.get("cache_creation")
        if isinstance(split, dict):
            self.write_5m += split.get("ephemeral_5m_input_tokens") or 0
            self.write_1h += split.get("ephemeral_1h_input_tokens") or 0
        else:
            self.write_5m += usage.get("cache_creation_input_tokens") or 0
        self.contexts.add(context)
        if woken:
            self.woken += self.equivalent - before

    @property
    def equivalent(self) -> float:
        """Input-price tokens: what the input side costs, priced as uncached input."""
        return self.input + WRITE_5M * self.write_5m + WRITE_1H * self.write_1h + READ * self.read

    def row(self) -> dict:
        return {
            "contexts": len(self.contexts), "calls": self.calls, "input": self.input,
            "cache_write": self.write_5m + self.write_1h, "cache_read": self.read,
            "output": self.output, "input_equivalent": round(self.equivalent),
            "woken_equivalent": round(self.woken),
        }


class Report:
    def __init__(self, since: str | None = None, session: str = "") -> None:
        self.since = since
        self.session = session
        self.groups: dict[tuple[str, str], Usage] = {}
        self.seen: set[str] = set()
        self.skipped = 0
        self.span: list[str] = []
        self.spawns: list[dict] = []
        self.results: dict[tuple[str, str], list[int]] = {}
        self.reads: dict[tuple[str, str], int] = {}
        self.read_calls: set[str] = set()

    def add(self, command: str, agent: str, entry: dict, context: str, woken: bool = False) -> dict | None:
        """Count a response once, and return its usage when it was counted."""
        message = entry.get("message")
        if entry.get("type") != "assistant" or not isinstance(message, dict):
            return None
        usage = message.get("usage")
        if not isinstance(usage, dict):
            return None
        if self.since and str(entry.get("timestamp", self.since))[:10] < self.since:
            return None
        # A response is written once per content block, each line carrying the same usage.
        key = message.get("id") or entry.get("requestId") or entry.get("uuid")
        if key in self.seen:
            return None
        if key:
            self.seen.add(key)
        self.groups.setdefault((command, agent), Usage()).add(usage, context, woken)
        if day := str(entry.get("timestamp", ""))[:10]:
            self.span = [min(self.span[0], day), max(self.span[1], day)] if self.span else [day, day]
        return usage

    def read_project(self, project: Path) -> None:
        for main in sorted(project.glob(glob.escape(self.session) + "*.jsonl")):
            self.read_session(main)

    def read_session(self, main: Path) -> None:
        session = main.stem
        boundaries: list[tuple[str, str, bool]] = []
        types: dict[str, str] = {}
        command, prompt, woken = NO_COMMAND, 0, False
        tools: dict[str, str] = {}
        for entry in self.entries(main):
            # A wake is mostly a background agent or watch reporting back inside the command's own run.
            if is_prompt(entry) and command_of(entry) in CARRY_ON:
                pass
            elif is_prompt(entry):
                command, prompt, woken = command_of(entry), prompt + 1, False
                boundaries.append((str(entry.get("timestamp", "")), command, woken))
            elif wake_of(entry) and not woken:
                woken = True
                boundaries.append((str(entry.get("timestamp", "")), command, woken))
            # A plugin's skill hands on to the next; one of this repository's commands keeps what it loads.
            elif (command == NO_COMMAND or command.startswith("skill:") and ":" in command[6:]) \
                    and not entry.get("isSidechain") \
                    and (skill := skill_of(entry)) and command != "skill:" + skill:
                command = "skill:" + skill
                boundaries.append((str(entry.get("timestamp", "")), command, woken))
            if not entry.get("isSidechain"):
                self.count_results(command, entry, tools)
            self.count_reads(entry, SUBAGENT if entry.get("isSidechain") else MAIN)
            result = entry.get("toolUseResult")
            if isinstance(result, dict) and result.get("agentId") and result.get("agentType"):
                types[result["agentId"]] = result["agentType"]
            if entry.get("isSidechain"):
                self.add(command, SUBAGENT, entry, f"{session}/{entry.get('agentId', 'inline')}", woken)
            else:
                self.add(command, MAIN, entry, f"{session}#{prompt}", woken)
        for transcript in sorted((main.parent / session / "subagents").glob("agent-*.jsonl")):
            agent_id = transcript.stem.removeprefix("agent-")
            meta = read_meta(transcript.with_suffix(".meta.json"))
            agent = text_field(meta, "agentType") or types.get(agent_id) or SUBAGENT
            entries = list(self.entries(transcript))
            started = next((str(e["timestamp"]) for e in entries if e.get("timestamp")), "")
            spawned_in, after_wake = command_at(boundaries, started)
            spawn = Usage()
            for entry in entries:
                self.count_reads(entry, SUBAGENT)
                if counted := self.add(spawned_in, agent, entry, f"{session}/{agent_id}", after_wake):
                    spawn.add(counted, agent_id, after_wake)
            if spawn.calls:
                self.spawns.append({"command": spawned_in, "agent": agent, "started": started[:10],
                                    "description": text_field(meta, "description") or "", **spawn.row()})

    def count_results(self, command: str, entry: dict, tools: dict[str, str]) -> None:
        """Note each tool call's name, and add each tool result's characters to its tool's count."""
        content = (entry.get("message") or {}).get("content")
        if not isinstance(content, list):
            return
        if self.since and str(entry.get("timestamp", self.since))[:10] < self.since:
            return
        for block in content:
            if not isinstance(block, dict):
                continue
            if block.get("type") == "tool_use" and isinstance(block.get("id"), str):
                tools[block["id"]] = str(block.get("name") or "?")
            elif block.get("type") == "tool_result":
                tool = tools.get(str(block.get("tool_use_id")), "?")
                counted = self.results.setdefault((command, tool), [0, 0])
                counted[0] += 1
                counted[1] += len(result_text(block.get("content")))

    def count_reads(self, entry: dict, who: str) -> None:
        """Count each markdown file a response opens, by its absolute path, with Read or a shell command."""
        content = (entry.get("message") or {}).get("content") if entry.get("type") == "assistant" else None
        if not isinstance(content, list):
            return
        if self.since and str(entry.get("timestamp", self.since))[:10] < self.since:
            return
        cwd = str(entry.get("cwd") or "")
        for block in content:
            if isinstance(block, dict) and block.get("type") == "tool_use":
                # A subagent's call can be written both as a sidechain line and in its own transcript.
                if (call := block.get("id")) in self.read_calls:
                    continue
                if call:
                    self.read_calls.add(call)
                for path in markdown_read(str(block.get("name")), block.get("input")):
                    key = (absolute(path, cwd), who)
                    self.reads[key] = self.reads.get(key, 0) + 1

    def entries(self, path: Path):
        with path.open(encoding="utf-8", errors="replace") as lines:
            for line in lines:
                try:
                    entry = json.loads(line)
                except ValueError:
                    self.skipped += 1
                    continue
                if isinstance(entry, dict):
                    yield entry

    def rows(self) -> list[dict]:
        rows = [{"command": c, "agent": a, **u.row()} for (c, a), u in self.groups.items()]
        rows.sort(key=lambda r: r["input_equivalent"], reverse=True)
        total = Usage()
        for usage in self.groups.values():
            for name in ("calls", "input", "write_5m", "write_1h", "read", "output", "woken"):
                setattr(total, name, getattr(total, name) + getattr(usage, name))
            total.contexts |= usage.contexts
        return [*rows, {"command": "TOTAL", "agent": "", **total.row()}]


def result_text(content) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "".join(b.get("text", "") for b in content if isinstance(b, dict) and isinstance(b.get("text"), str))
    return ""


def markdown_read(tool: str, given) -> list[str]:
    """The markdown paths one tool call reads: Read's file, or what cat, head, tail, sed or Get-Content print."""
    if not isinstance(given, dict):
        return []
    if tool == "Read":
        path = given.get("file_path")
        return [path] if isinstance(path, str) and path.lower().endswith(".md") else []
    command = given.get("command")
    if tool not in ("Bash", "PowerShell") or not isinstance(command, str):
        return []
    found = []
    for segment in SHELL_SEGMENT.split(command):
        words = [w.strip("\"'") for w in SHELL_WORD.findall(segment)]
        if words and words[0].lower() in SHELL_READERS:
            found += [w for w in words[1:] if w.lower().endswith(".md") and not w.startswith("-")]
    return found


def slashed(path: str) -> str:
    return path.replace("\\", "/")


def absolute(path: str, cwd: str) -> str:
    """A path with forward slashes, made absolute against the call's working directory."""
    path = slashed(path)
    if not (path.startswith("/") or re.match(r"[A-Za-z]:/", path)):
        path = slashed(cwd).rstrip("/") + "/" + path
    return posixpath.normpath(path)


def repo_relative(path: str, root: str) -> str | None:
    """A read's path inside the checkout, a worktree under .claude/worktrees or a sibling fork, else None."""
    root = posixpath.normpath(slashed(root))
    windows = re.match(r"[A-Za-z]:/", root) is not None
    inside = re.match(re.escape(root.lower() if windows else root) + r"(?:-[^/]+|/\.claude/worktrees/[^/]+)?/(.+)$",
                      path.lower() if windows else path)
    return path[len(path) - len(inside.group(1)):] if inside else None


def tracked_docs(root: Path) -> dict[str, str]:
    """Every markdown file git tracks in the checkout, with its text."""
    listed = subprocess.run(["git", "-c", "core.quotePath=off", "ls-files", "-z", "*.md"], cwd=root,
                            capture_output=True, text=True, encoding="utf-8", check=True).stdout
    docs = {}
    for doc in filter(None, listed.split("\0")):
        try:
            docs[doc] = (root / doc).read_text(encoding="utf-8", errors="replace")
        except OSError:
            docs[doc] = ""
    return docs


def named_in(source: str, text: str, docs: dict[str, str]) -> set[str]:
    """The tracked documents a file's text names: by a path from the root or from the file, by a unique base
    name, or by a template such as `docs/commands/<name>.md`, which names every file it fits."""
    names: dict[str, list[str]] = {}
    for doc in docs:
        names.setdefault(posixpath.basename(doc), []).append(doc)
    found = set()
    for mention in set(MENTION.findall(text)):
        if PLACEHOLDER.search(mention):
            # A bare `<name>.md` fits every file, so only a template with a directory names any.
            if "/" in mention:
                parts = PLACEHOLDER.split(mention.lstrip("./"))
                fits = re.compile(r"(?:^|/)" + "[^/]+".join(map(re.escape, parts)) + "$")
                found |= {doc for doc in docs if fits.search(doc)}
            continue
        # A bare name is read beside the file citing it, never as the root's file of that name.
        for candidate in (mention if "/" in mention else "", posixpath.join(posixpath.dirname(source), mention)):
            candidate = posixpath.normpath(candidate)
            if candidate in docs:
                found.add(candidate)
        if "/" not in mention and len(names.get(mention, [])) == 1:
            found.add(names[mention][0])
    found.discard(source)
    return found


def doc_rows(docs: dict[str, str], reads: dict[tuple[str, str], int], root: str) -> list[dict]:
    """One row per tracked document: who names it, and how often a main session and a subagent opened it."""
    entry = {doc for doc in docs if ENTRY.fullmatch(doc)}
    from_entry = set().union(*(named_in(doc, docs[doc], docs) for doc in entry))
    from_claude_md = named_in("CLAUDE.md", docs.get("CLAUDE.md", ""), docs)
    opened: dict[tuple[str, str], int] = {}
    for (path, who), count in reads.items():
        if (doc := repo_relative(path, root)) in docs:
            opened[doc, who] = opened.get((doc, who), 0) + count
    rows = []
    for doc in docs:
        if doc == "CLAUDE.md":
            continue
        link = LINKS[0] if doc in entry else LINKS[1] if doc in from_entry else \
            LINKS[2] if doc in from_claude_md else LINKS[3]
        main, agents = opened.get((doc, MAIN), 0), opened.get((doc, SUBAGENT), 0)
        rows.append({"doc": doc, "named_by": link, "reads": main + agents, "main": main, "subagents": agents})
    rows.sort(key=lambda r: (LINKS.index(r["named_by"]), -r["reads"], r["doc"]))
    return rows


def link_summary(rows: list[dict]) -> list[str]:
    lines = []
    for link in LINKS:
        group = [r["reads"] for r in rows if r["named_by"] == link]
        if group:
            lines.append(f"{link}: {len(group)} files, {sum(group) / len(group):.1f} reads each, "
                         f"{group.count(0)} never read")
    return lines


def is_prompt(entry: dict) -> bool:
    """A person's prompt: tool results and injected meta messages are user entries too."""
    if entry.get("type") != "user" or entry.get("isSidechain") or entry.get("isMeta"):
        return False
    if entry.get("isCompactSummary"):
        return False
    origin = entry.get("origin")
    if isinstance(origin, dict) and origin.get("kind") != "human":
        return False
    content = (entry.get("message") or {}).get("content")
    if isinstance(content, list):
        texts = [b.get("text", "") for b in content if isinstance(b, dict) and b.get("type") == "text"]
        if not texts:
            return False
        content = " ".join(texts)
    # A `!` command's input and output, a local command's output and an interrupt are written as user text.
    return isinstance(content, str) and not content.lstrip().startswith(INJECTED)


def wake_of(entry: dict) -> str | None:
    """The kind of a prompt no person typed: a task notification, a scheduled wake."""
    origin = entry.get("origin")
    if entry.get("type") != "user" or entry.get("isSidechain") or entry.get("isMeta") or not isinstance(origin, dict):
        return None
    kind = origin.get("kind")
    if kind == "human" or not isinstance(kind, str) or not kind:
        return None
    return kind if is_prompt({**entry, "origin": None}) else None


def command_of(entry: dict) -> str:
    content = entry["message"]["content"]
    if isinstance(content, list):
        content = " ".join(b.get("text", "") for b in content if isinstance(b, dict))
    found = COMMAND.search(content)
    return "/" + found.group(1) if found else NO_COMMAND


def skill_of(entry: dict) -> str | None:
    """The skill a response loads with the Skill tool, which a typed slash command does not need."""
    content = (entry.get("message") or {}).get("content") if entry.get("type") == "assistant" else None
    for block in content if isinstance(content, list) else ():
        if isinstance(block, dict) and block.get("type") == "tool_use" and block.get("name") == "Skill":
            name = (block.get("input") or {}).get("skill")
            if isinstance(name, str) and name:
                return name.lstrip("/")
    return None


def command_at(boundaries: list[tuple[str, str, bool]], timestamp: str) -> tuple[str, bool]:
    """The command whose prompt last preceded the timestamp, and whether a wake had come since."""
    index = bisect.bisect_right([t for t, _, _ in boundaries], timestamp) - 1
    return boundaries[index][1:] if index >= 0 else (NO_COMMAND, False)


def read_meta(meta: Path) -> dict:
    try:
        found = json.loads(meta.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return found if isinstance(found, dict) else {}


def text_field(meta: dict, name: str) -> str | None:
    found = meta.get(name)
    return found if isinstance(found, str) and found else None


def project_name(path: str) -> str:
    """Claude Code's name for a directory's transcripts: its path with every other character a dash."""
    return re.sub(r"[^A-Za-z0-9]", "-", path)


def default_project(root: Path) -> Path:
    return Path.home() / ".claude" / "projects" / project_name(str(root.resolve()))


def default_projects(cwd: Path) -> list[Path]:
    """The main checkout's transcript directory, then each worktree's: under .claude/worktrees or a sibling fork."""
    main = default_project(Path(WORKTREE.sub("", str(cwd.resolve()))))
    return [main, *sorted(main.parent.glob(main.name + "-*"))]


ROWS = ("command", "agent", "contexts", "calls", "input", "cache_write", "cache_read", "output", "input_equivalent",
        "woken_equivalent")
DOCS = ("doc", "named_by", "reads", "main", "subagents")
RESULTS = ("command", "tool", "results", "characters", "approx_tokens")
SPAWNS = ("command", "agent", "started", "calls", "input_equivalent", "description")


def render(rows: list[dict], columns: tuple[str, ...] = ROWS) -> str:
    cells = [list(columns)] + [[f"{r[c]:,}" if isinstance(r[c], int) else r[c] for c in columns] for r in rows]
    widths = [max(len(row[i]) for row in cells) for i in range(len(columns))]
    left = {"command", "agent", "started", "description", "tool", "doc", "named_by"}
    lines = ["  ".join(cell.ljust(w) if c in left else cell.rjust(w) for c, cell, w in zip(columns, row, widths))
             for row in cells]
    return "\n".join(line.rstrip() for line in lines)


def main(argv: list[str] | None = None) -> int:
    # A redirected Windows stdout is cp1252, and a spawn's description is whatever the model wrote.
    if isinstance(sys.stdout, io.TextIOWrapper):
        sys.stdout.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("projects", nargs="*", type=Path,
                        help="transcript directories; defaults to this checkout's under ~/.claude/projects")
    parser.add_argument("--since", help="only responses on or after this date, YYYY-MM-DD")
    parser.add_argument("--json", action="store_true", help="print the rows as JSON")
    views = parser.add_mutually_exclusive_group()
    views.add_argument("--spawns", type=int, metavar="N",
                       help="list the N costliest subagents, each with the task it was given, instead")
    views.add_argument("--tools", type=int, metavar="N",
                       help="list the N commands and tools whose results put the most text into main sessions")
    views.add_argument("--docs", action="store_true",
                       help="list every tracked markdown file with who names it and how often it was opened")
    parser.add_argument("--session", default="", metavar="ID",
                        help="only the session whose id starts with ID, with its subagents")
    args = parser.parse_args(argv)
    if args.since and not re.fullmatch(r"\d{4}-\d{2}-\d{2}", args.since):
        parser.error("--since takes YYYY-MM-DD")
    projects = args.projects or [p for p in default_projects(Path.cwd()) if p.is_dir()]
    missing = [p for p in args.projects if not p.is_dir()] if args.projects else []
    if missing or not projects:
        print(f"no transcript directory at {(missing or default_projects(Path.cwd()))[0]}", file=sys.stderr)
        return 2
    report = Report(args.since, args.session)
    for project in projects:
        report.read_project(project)
    print(f"read {projects[0]}" + (f" and {len(projects) - 1} more" if len(projects) > 1 else ""), file=sys.stderr)
    if args.docs:
        root = Path(WORKTREE.sub("", str(Path.cwd().resolve())))
        try:
            docs = tracked_docs(root)
        except (OSError, subprocess.CalledProcessError):
            print(f"no git checkout at {root} to list the markdown files of", file=sys.stderr)
            return 2
        rows, columns = doc_rows(docs, report.reads, str(root)), DOCS
        for line in link_summary(rows):
            print(line, file=sys.stderr)
    elif args.tools is not None:
        rows = [{"command": c, "tool": t, "results": n, "characters": size, "approx_tokens": size // 4}
                for (c, t), (n, size) in report.results.items()]
        rows = sorted(rows, key=lambda r: r["characters"], reverse=True)[:args.tools]
        columns = RESULTS
    elif args.spawns is not None:
        rows = sorted(report.spawns, key=lambda r: r["input_equivalent"], reverse=True)[:args.spawns]
        columns = SPAWNS
    else:
        rows, columns = report.rows(), ROWS
    print(json.dumps(rows, indent=2) if args.json else render(rows, columns))
    if report.span:
        print(f"responses from {report.span[0]} to {report.span[1]}", file=sys.stderr)
    if report.skipped:
        print(f"{report.skipped} unreadable transcript lines skipped", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
