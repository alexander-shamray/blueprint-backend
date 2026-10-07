#!/usr/bin/env python3
"""Sum Claude Code's recorded token usage per slash command and agent type.

    python tools/token-usage/token_usage.py [PROJECT_DIR ...] [--since 2026-10-01] [--json]
"""

from __future__ import annotations

import argparse
import bisect
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

NO_COMMAND = "(no command)"
MAIN = "main"
SUBAGENT = "subagent"
COMMAND = re.compile(r"<command-name>/?([^<\s]+)</command-name>")

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
    contexts: set[str] = field(default_factory=set)

    def add(self, usage: dict, context: str) -> None:
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

    @property
    def equivalent(self) -> float:
        """Input-price tokens: what the input side costs, priced as uncached input."""
        return self.input + WRITE_5M * self.write_5m + WRITE_1H * self.write_1h + READ * self.read

    def row(self) -> dict:
        return {
            "contexts": len(self.contexts), "calls": self.calls, "input": self.input,
            "cache_write": self.write_5m + self.write_1h, "cache_read": self.read,
            "output": self.output, "input_equivalent": round(self.equivalent),
        }


class Report:
    def __init__(self, since: str | None = None) -> None:
        self.since = since
        self.groups: dict[tuple[str, str], Usage] = {}
        self.seen: set[str] = set()
        self.skipped = 0

    def add(self, command: str, agent: str, entry: dict, context: str) -> None:
        message = entry.get("message")
        if entry.get("type") != "assistant" or not isinstance(message, dict):
            return
        usage = message.get("usage")
        if not isinstance(usage, dict):
            return
        if self.since and str(entry.get("timestamp", self.since))[:10] < self.since:
            return
        # A response is written once per content block, each line carrying the same usage.
        key = message.get("id") or entry.get("requestId") or entry.get("uuid")
        if key in self.seen:
            return
        if key:
            self.seen.add(key)
        self.groups.setdefault((command, agent), Usage()).add(usage, context)

    def read_project(self, project: Path) -> None:
        for main in sorted(project.glob("*.jsonl")):
            self.read_session(main)

    def read_session(self, main: Path) -> None:
        session = main.stem
        boundaries: list[tuple[str, str]] = []
        types: dict[str, str] = {}
        command, prompt = NO_COMMAND, 0
        for entry in self.entries(main):
            if is_prompt(entry):
                command, prompt = command_of(entry), prompt + 1
                boundaries.append((str(entry.get("timestamp", "")), command))
            result = entry.get("toolUseResult")
            if isinstance(result, dict) and result.get("agentId") and result.get("agentType"):
                types[result["agentId"]] = result["agentType"]
            if entry.get("isSidechain"):
                self.add(command, SUBAGENT, entry, f"{session}/{entry.get('agentId', 'inline')}")
            else:
                self.add(command, MAIN, entry, f"{session}#{prompt}")
        for transcript in sorted((main.parent / session / "subagents").glob("agent-*.jsonl")):
            agent_id = transcript.stem.removeprefix("agent-")
            agent = agent_type(transcript.with_suffix(".meta.json")) or types.get(agent_id) or SUBAGENT
            entries = list(self.entries(transcript))
            started = next((str(e["timestamp"]) for e in entries if e.get("timestamp")), "")
            spawned_in = command_at(boundaries, started)
            for entry in entries:
                self.add(spawned_in, agent, entry, f"{session}/{agent_id}")

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
            for name in ("calls", "input", "write_5m", "write_1h", "read", "output"):
                setattr(total, name, getattr(total, name) + getattr(usage, name))
            total.contexts |= usage.contexts
        return [*rows, {"command": "TOTAL", "agent": "", **total.row()}]


def is_prompt(entry: dict) -> bool:
    """A person's prompt: tool results and injected meta messages are user entries too."""
    if entry.get("type") != "user" or entry.get("isSidechain") or entry.get("isMeta"):
        return False
    origin = entry.get("origin")
    if isinstance(origin, dict) and origin.get("kind") != "human":
        return False
    content = (entry.get("message") or {}).get("content")
    if isinstance(content, str):
        return True
    return isinstance(content, list) and any(
        isinstance(b, dict) and b.get("type") == "text" for b in content)


def command_of(entry: dict) -> str:
    content = entry["message"]["content"]
    if isinstance(content, list):
        content = " ".join(b.get("text", "") for b in content if isinstance(b, dict))
    found = COMMAND.search(content)
    return "/" + found.group(1) if found else NO_COMMAND


def command_at(boundaries: list[tuple[str, str]], timestamp: str) -> str:
    """The command whose prompt last preceded the timestamp."""
    index = bisect.bisect_right([t for t, _ in boundaries], timestamp) - 1
    return boundaries[index][1] if index >= 0 else NO_COMMAND


def agent_type(meta: Path) -> str | None:
    try:
        found = json.loads(meta.read_text(encoding="utf-8")).get("agentType")
    except (OSError, ValueError, AttributeError):
        return None
    return found if isinstance(found, str) and found else None


def default_project(root: Path) -> Path:
    """Claude Code's transcript directory for a checkout: its path with every other character a dash."""
    return Path.home() / ".claude" / "projects" / re.sub(r"[^A-Za-z0-9]", "-", str(root.resolve()))


def render(rows: list[dict]) -> str:
    columns = ("command", "agent", "contexts", "calls", "input", "cache_write", "cache_read", "output",
               "input_equivalent")
    cells = [list(columns)] + [[f"{r[c]:,}" if isinstance(r[c], int) else r[c] for c in columns] for r in rows]
    widths = [max(len(row[i]) for row in cells) for i in range(len(columns))]
    lines = ["  ".join(cell.ljust(w) if i < 2 else cell.rjust(w) for i, (cell, w) in enumerate(zip(row, widths)))
             for row in cells]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("projects", nargs="*", type=Path,
                        help="transcript directories; defaults to this checkout's under ~/.claude/projects")
    parser.add_argument("--since", help="only responses on or after this date, YYYY-MM-DD")
    parser.add_argument("--json", action="store_true", help="print the rows as JSON")
    args = parser.parse_args(argv)
    if args.since and not re.fullmatch(r"\d{4}-\d{2}-\d{2}", args.since):
        parser.error("--since takes YYYY-MM-DD")
    projects = args.projects or [default_project(Path.cwd())]
    report = Report(args.since)
    for project in projects:
        if not project.is_dir():
            print(f"no transcript directory at {project}", file=sys.stderr)
            return 2
        report.read_project(project)
    rows = report.rows()
    print(json.dumps(rows, indent=2) if args.json else render(rows))
    if report.skipped:
        print(f"{report.skipped} unreadable transcript lines skipped", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
