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
import tempfile
from dataclasses import dataclass, field, replace
from pathlib import Path

NO_COMMAND = "(no command)"
UNKNOWN_MODEL = "(unknown)"
MAIN = "main"
SUBAGENT = "subagent"
COMMAND = re.compile(r"<command-name>/?([^<\s]+)</command-name>")
INJECTED = ("<bash-", "<local-command-", "[Request interrupted")
# Harness commands that act on the session, typed mid-run, after which the run they interrupted goes on.
CARRY_ON = {"/compact", "/autocompact", "/reload-plugins", "/context", "/cost", "/status", "/model", "/effort",
            "/fast", "/config", "/permissions", "/memory", "/mcp", "/hooks", "/agents", "/plugin", "/help"}
WORKTREE = re.compile(r"[\\/]\.claude[\\/]worktrees[\\/].*$")
# tools/quality-sets runs each `claude -p` in a detached worktree in the system temp directory, named by its
# review_workspace and sweep_root; a transcript directory named for one is counted under that run's command.
RUNNERS = (("quality-sets:review", r"-qs-review-[A-Za-z0-9-]+-tree"), ("quality-sets:sweep", r"-secsweep-[0-9a-f]{6}"))
# A shell command whose markdown arguments it prints: the reads that do not go through the Read tool.
SHELL_READERS = {"cat", "head", "tail", "sed", "less", "more", "type", "gc", "get-content"}
SHELL_COMMAND = re.compile(r"&&|\|\||[;\n]")
SED_IN_PLACE = re.compile(r"--in-place(?:=.*)?|-[A-Za-z]*i.*")
SHELL_WORD = re.compile(r'"[^"]*"|\'[^\']*\'|\S+')
SHELL_QUOTED = re.compile(r'"[^"]*"|\'[^\']*\'')
SHELL_PIPE = re.compile(r"\|")
# A span is the lines an open printed, (first, last) from 1, with last None for the end and a negative first
# for the last lines; a pattern's lines are placed against the file's text, and None is a part the report
# cannot place, such as a sed script it does not parse.
SED_QUIET = re.compile(r"-[A-Za-z]*n[A-Za-z]*|--quiet|--silent")
SED_EXTENDED = re.compile(r"-[A-Za-z]*[Er][A-Za-z]*|--regexp-extended")
SED_ADDRESS = r"\d+|\$|/(?:[^/\\]|\\.)*/"
SED_LINES = re.compile(rf"({SED_ADDRESS})(?:,({SED_ADDRESS}))?p")
GREPS = {"grep", "egrep", "fgrep"}
GREP_NUMBERED = re.compile(r"-[A-Za-z]*n[A-Za-z0-9]*|--line-number")
NUMBERED_LINE = re.compile(r"(\d+)[:-]")
HEAD_TAIL_COUNT = re.compile(r"-(\d+)|-n(\+?\d+)|--lines=(\+?\d+)")
READ_LIMIT = 2000
# Where a run starts: what a command, an agent or a skill names, it is handed.
ENTRY = re.compile(r"\.claude/(?:commands/[^/]+|agents/[^/]+|skills/[^/]+/SKILL)\.md")
MENTION = re.compile(r"[\w./<>{}-]*[\w>}]\.md\b")
PLACEHOLDER = re.compile(r"<[^<>/]+>|\{[^{}/]+\}")
LINKS = ("entry point", "named by entry", "CLAUDE.md only", "none")

# Anthropic's prompt-caching prices as multiples of the base input price.
WRITE_5M = 1.25
WRITE_1H = 2.0
READ = 0.1


@dataclass(frozen=True)
class Addressed:
    """A sed address range: from each line the start matches to the next line the stop matches, to a line number
    or to the end; or, single, each line the start matches. A pattern is the text between its slashes."""
    start: int | str
    stop: int | str | None
    single: bool = False
    extended: bool = False


@dataclass(frozen=True)
class Grepped:
    """What a grep after a read printed of its span: the call's result, placed by its line numbers when the grep
    numbered them, else by the lines of the span whose text it printed, in order."""
    within: Span
    numbered: bool
    text: str | None = None


@dataclass(frozen=True)
class Piped:
    """A stage that reads the stage before it: inner placed against the lines outer printed, in their order."""
    outer: Span
    inner: Span


Span = tuple[int, int | None] | Addressed | Grepped | Piped | None


@dataclass
class Usage:
    calls: int = 0
    input: int = 0
    write_5m: int = 0
    write_1h: int = 0
    read: int = 0
    output: int = 0
    woken: float = 0.0
    woken_write: float = 0.0
    contexts: set[str] = field(default_factory=set)

    def add(self, usage: dict, context: str, woken: bool = False) -> None:
        before, was = self.equivalent, (self.write_5m, self.write_1h)
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
            self.woken_write += WRITE_5M * (self.write_5m - was[0]) + WRITE_1H * (self.write_1h - was[1])

    @property
    def equivalent(self) -> float:
        """Input-price tokens: what the input side costs, priced as uncached input."""
        return self.input + WRITE_5M * self.write_5m + WRITE_1H * self.write_1h + READ * self.read

    @property
    def mean_context(self) -> int:
        """Prompt tokens a call sent on average: fresh input, cache writes and cache reads."""
        sent = self.input + self.write_5m + self.write_1h + self.read
        return round(sent / self.calls) if self.calls else 0

    def row(self) -> dict:
        return {
            "contexts": len(self.contexts), "calls": self.calls, "mean_context": self.mean_context,
            "input": self.input,
            "cache_write": self.write_5m + self.write_1h, "cache_read": self.read,
            "output": self.output, "input_equivalent": round(self.equivalent),
            "woken_equivalent": round(self.woken), "woken_write": round(self.woken_write),
        }


class Report:
    def __init__(self, since: str | None = None, session: str = "") -> None:
        self.since = since
        self.session = session
        self.groups: dict[tuple[str, str], Usage] = {}
        self.by_model: dict[tuple[str, str, str], Usage] = {}
        self.seen: set[str] = set()
        self.skipped = 0
        self.span: list[str] = []
        self.spawns: list[dict] = []
        self.results: dict[tuple[str, str], list[int]] = {}
        self.reads: dict[tuple[str, str], int] = {}
        self.opens: list[tuple[str, str, str, Span]] = []
        self.failed: set[str] = set()
        self.read_calls: set[str] = set()
        self.grepped: set[str] = set()
        self.printed: dict[str, str] = {}
        self.trees: set[str] = set()

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
        model = text_field(message, "model") or UNKNOWN_MODEL
        self.by_model.setdefault((command, agent, model), Usage()).add(usage, context, woken)
        if day := str(entry.get("timestamp", ""))[:10]:
            self.span = [min(self.span[0], day), max(self.span[1], day)] if self.span else [day, day]
        return usage

    def read_project(self, project: Path, start: str = NO_COMMAND) -> None:
        for main in sorted(project.glob(glob.escape(self.session) + "*.jsonl")):
            self.read_session(main, start)

    def read_session(self, main: Path, start: str = NO_COMMAND) -> None:
        """Count one session; start is the command a prompt naming none is counted under, a run's for its own."""
        session = main.stem
        boundaries: list[tuple[str, str, bool]] = []
        types: dict[str, str] = {}
        command, prompt, woken = start, 0, False
        tools: dict[str, str] = {}
        agent, who = MAIN, MAIN
        for entry in self.entries(main):
            # A session started with --agent is that agent's own, as each tools/quality-sets run is, and reads as
            # the subagent an interactive run would have spawned.
            if entry.get("type") == "agent-setting" and text_field(entry, "agentSetting"):
                agent, who = entry["agentSetting"], SUBAGENT
            if start != NO_COMMAND and text_field(entry, "cwd"):
                self.trees.add(posixpath.normpath(slashed(entry["cwd"])))
            # A wake is mostly a background agent or watch reporting back inside the command's own run.
            if is_prompt(entry) and command_of(entry) in CARRY_ON:
                pass
            elif is_prompt(entry):
                named = command_of(entry)
                command, prompt, woken = start if named == NO_COMMAND else named, prompt + 1, False
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
            if entry.get("isSidechain"):
                self.count_reads(entry, SUBAGENT, f"{session}/{entry.get('agentId', 'inline')}")
            else:
                self.count_reads(entry, who, session)
            result = entry.get("toolUseResult")
            if isinstance(result, dict) and result.get("agentId") and result.get("agentType"):
                types[result["agentId"]] = result["agentType"]
            if entry.get("isSidechain"):
                self.add(command, SUBAGENT, entry, f"{session}/{entry.get('agentId', 'inline')}", woken)
            else:
                self.add(command, agent, entry, f"{session}#{prompt}", woken)
        for transcript in sorted((main.parent / session / "subagents").glob("agent-*.jsonl")):
            agent_id = transcript.stem.removeprefix("agent-")
            meta = read_meta(transcript.with_suffix(".meta.json"))
            agent = text_field(meta, "agentType") or types.get(agent_id) or SUBAGENT
            entries = list(self.entries(transcript))
            started = next((str(e["timestamp"]) for e in entries if e.get("timestamp")), "")
            spawned_in, after_wake = command_at(boundaries, started)
            spawn = Usage()
            for entry in entries:
                self.count_reads(entry, SUBAGENT, f"{session}/{agent_id}")
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

    def count_reads(self, entry: dict, who: str, context: str) -> None:
        """Count each markdown file a response opens, by its absolute path, with Read or a shell command; keep each
        open's span under its context, and note the calls whose result was an error."""
        content = (entry.get("message") or {}).get("content")
        if not isinstance(content, list):
            return
        if self.since and str(entry.get("timestamp", self.since))[:10] < self.since:
            return
        if entry.get("type") == "user":
            for block in content:
                if not isinstance(block, dict) or block.get("type") != "tool_result":
                    continue
                call, text = str(block.get("tool_use_id")), result_text(block.get("content"))
                # A shell's non-zero exit is an error result too, but its command ran and printed what it read.
                if block.get("is_error") and not text.startswith("Exit code"):
                    self.failed.add(call)
                if call in self.grepped:
                    self.printed[call] = text
            return
        if entry.get("type") != "assistant":
            return
        cwd = str(entry.get("cwd") or "")
        for block in content:
            if isinstance(block, dict) and block.get("type") == "tool_use":
                # A subagent's call can be written both as a sidechain line and in its own transcript.
                if (call := block.get("id")) in self.read_calls:
                    continue
                if call:
                    self.read_calls.add(call)
                for path, span in markdown_read(str(block.get("name")), block.get("input")):
                    key = (absolute(path, cwd), who)
                    self.reads[key] = self.reads.get(key, 0) + 1
                    self.opens.append((str(call), key[0], context, span))
                    if isinstance(span, Grepped):
                        self.grepped.add(str(call))

    def kept_opens(self) -> list[tuple[str, str, Span]]:
        """Each open whose call did not fail, as (path, context, span): a refused Read put none of the file in
        context. A grep's span takes its call's result, unless the call made another open, whose output the
        result mixes in."""
        per_call: dict[str, int] = {}
        for call, *_ in self.opens:
            per_call[call] = per_call.get(call, 0) + 1
        kept = []
        for call, path, context, span in self.opens:
            if call in self.failed:
                continue
            if isinstance(span, Grepped):
                span = replace(span, text=self.printed.get(call)) if per_call[call] == 1 else None
            kept.append((path, context, span))
        return kept

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

    def rows(self, by_model: bool = False) -> list[dict]:
        if by_model:
            rows = [{"command": c, "agent": a, "model": m, **u.row()} for (c, a, m), u in self.by_model.items()]
            usages = self.by_model.values()
        else:
            rows = [{"command": c, "agent": a, **u.row()} for (c, a), u in self.groups.items()]
            usages = self.groups.values()
        rows.sort(key=lambda r: r["input_equivalent"], reverse=True)
        total = Usage()
        for usage in usages:
            for name in ("calls", "input", "write_5m", "write_1h", "read", "output", "woken", "woken_write"):
                setattr(total, name, getattr(total, name) + getattr(usage, name))
            total.contexts |= usage.contexts
        return [*rows, {"command": "TOTAL", "agent": "", **({"model": ""} if by_model else {}), **total.row()}]


def result_text(content) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "".join(b.get("text", "") for b in content if isinstance(b, dict) and isinstance(b.get("text"), str))
    return ""


def markdown_read(tool: str, given) -> list[tuple[str, Span]]:
    """The markdown paths one tool call reads, each with the span it printed: Read's file, from its offset for its
    limit, or what cat, head, tail, sed or Get-Content print, narrowed by the stages of a pipe after them."""
    if not isinstance(given, dict):
        return []
    if tool == "Read":
        path, offset, limit = given.get("file_path"), given.get("offset"), given.get("limit")
        first = max(offset, 1) if isinstance(offset, int) else 1
        last = first + (limit if isinstance(limit, int) and limit > 0 else READ_LIMIT) - 1
        return [(path, (first, last))] if isinstance(path, str) and path.lower().endswith(".md") else []
    command = given.get("command")
    if tool not in ("Bash", "PowerShell") or not isinstance(command, str):
        return []
    found: list[tuple[str, Span]] = []
    for line in unquoted_split(SHELL_COMMAND, command):
        piped: list[int] = []
        for stage in unquoted_split(SHELL_PIPE, line):
            words = [w.strip("\"'") for w in SHELL_WORD.findall(stage)]
            if not words:
                continue
            if words[0].lower() == "sed" and any(SED_IN_PLACE.fullmatch(w) for w in words):
                piped = []
                continue
            # A redirect's target is written, and so is everything after it.
            printed = next((i for i, w in enumerate(words) if ">" in w), len(words))
            files = [w for w in words[1:printed] if w.lower().endswith(".md") and not w.startswith("-")] \
                if words[0].lower() in SHELL_READERS else []
            if files:
                piped = list(range(len(found), len(found) + len(files)))
                found += [(file, stage_span(words[:printed])) for file in files]
            else:
                found = [(path, narrowed(span, stage_span(words))) if i in piped else (path, span)
                         for i, (path, span) in enumerate(found)]
    return found


def unquoted_split(separator: re.Pattern, text: str) -> list[str]:
    """A command split at each separator outside quotes, as a sed or grep pattern can hold `|` or `;`."""
    masked = SHELL_QUOTED.sub(lambda m: "\0" * len(m[0]), text)
    parts, start = [], 0
    for found in separator.finditer(masked):
        parts.append(text[start:found.start()])
        start = found.end()
    return [*parts, text[start:]]


def count_after(words: list[str], flags: set[str]) -> int | None:
    """The number after the first of the flags among a command's words, as -TotalCount 40 gives it."""
    for i, word in enumerate(words[:-1]):
        if word.lower() in flags and words[i + 1].isdigit():
            return int(words[i + 1])
    return None


def stage_span(words: list[str]) -> Span:
    """The span one stage of a command prints of what it reads, a file it names or the stage before it."""
    tool = words[0].lower()
    if tool in ("cat", "less", "more", "type"):
        return 1, None
    if tool in ("head", "tail"):
        count = "10"
        for i, word in enumerate(words[1:], 1):
            if word in ("-n", "--lines") and i + 1 < len(words):
                count = words[i + 1]
            elif match := HEAD_TAIL_COUNT.fullmatch(word):
                count = next(group for group in match.groups() if group)
        if not count.lstrip("+").isdigit():
            return None
        lines = int(count.lstrip("+"))
        return (1, lines) if tool == "head" else (lines, None) if count.startswith("+") else (-lines, None)
    if tool == "sed":
        quiet = any(SED_QUIET.fullmatch(w) for w in words[1:])
        script = next((w for w in words[1:] if not w.startswith("-") and not w.lower().endswith(".md")), "")
        if quiet and (match := SED_LINES.fullmatch(script)):
            return sed_span(match[1], match[2], any(SED_EXTENDED.fullmatch(w) for w in words[1:]))
        if not quiet and (match := re.fullmatch(r"(\d+)q", script)):
            return 1, int(match[1])
        return None if quiet else (1, None)
    if tool in ("gc", "get-content"):
        first, last = count_after(words, {"-totalcount", "-head", "-first"}), count_after(words, {"-tail", "-last"})
        return (1, first) if first else (-last, None) if last else (1, None)
    if tool in ("select-object", "select"):
        skip, first = count_after(words, {"-skip"}) or 0, count_after(words, {"-first"})
        if last := count_after(words, {"-last"}):
            return -last, None
        return (skip + 1, skip + first if first else None) if first or skip else None
    if tool in GREPS:
        return Grepped((1, None), any(GREP_NUMBERED.fullmatch(w) for w in words[1:]))
    return None


def sed_span(start: str, stop: str | None, extended: bool) -> Span:
    """The span a quiet sed prints for one address, or for a range of two."""
    if start == "$":
        return (-1, None) if stop is None else None
    if not start.startswith("/") and not (stop or "").startswith("/"):
        return int(start), None if stop == "$" else int(stop or start)
    def address(word: str) -> int | str | None:
        return word[1:-1] if word.startswith("/") else None if word == "$" else int(word)
    return Addressed(address(start), None if stop is None else address(stop), stop is None, extended)


def narrowed(outer: Span, inner: Span) -> Span:
    """The file's span a stage prints when it reads the stage before it, counting inner from that stage's start.
    A grep's span is its call's result, which every stage after it has already narrowed."""
    if isinstance(outer, Grepped):
        return outer
    if outer is None or inner is None:
        return None
    if isinstance(inner, Grepped):
        return replace(inner, within=outer)
    if outer == (1, None):
        return inner
    if not (isinstance(outer, tuple) and isinstance(inner, tuple)) or outer[0] < 1 or inner[0] < 1:
        return Piped(outer, inner)
    (first, last), (start, end) = outer, inner
    stop = None if end is None else first + end - 1
    return first + start - 1, last if stop is None else stop if last is None else min(last, stop)


def sed_pattern(body: str, extended: bool) -> re.Pattern | None:
    """A sed pattern as Python reads it: in a basic one `\\(` groups and `(` is itself, the reverse of extended."""
    if not extended:
        body = re.sub(r"\\([(){}+?|])|([(){}+?|])", lambda m: m[1] or "\\" + m[2], body)
    try:
        return re.compile(body)
    except re.error:
        return None


def addressed_lines(span: Addressed, lines: list[str]) -> set[int]:
    """The lines sed prints of a file for an address range, numbered from 1."""
    start = span.start if isinstance(span.start, int) else sed_pattern(span.start, span.extended)
    stop = sed_pattern(span.stop, span.extended) if isinstance(span.stop, str) else span.stop
    if start is None or isinstance(span.stop, str) and stop is None:
        return set()
    def hits(address: int | re.Pattern, number: int) -> bool:
        return number == address if isinstance(address, int) else address.search(lines[number - 1]) is not None
    printed, number = set(), 1
    while number <= len(lines):
        if not hits(start, number):
            number += 1
            continue
        if span.single:
            end = number
        elif isinstance(stop, int):
            # A stop at or before the line the start matched prints that line alone.
            end = min(max(stop, number), len(lines))
        elif stop is None:
            end = len(lines)
        else:
            end = next((n for n in range(number + 1, len(lines) + 1) if hits(stop, n)), len(lines))
        printed.update(range(number, end + 1))
        number = end + 1
    return printed


def span_lines(span: Span, lines: list[str]) -> set[int]:
    """The lines of a file, numbered from 1, that one open put in context."""
    if isinstance(span, Addressed):
        return addressed_lines(span, lines)
    if isinstance(span, Piped):
        within = sorted(span_lines(span.outer, lines))
        return {within[n - 1] for n in span_lines(span.inner, [lines[n - 1] for n in within])}
    if isinstance(span, Grepped):
        within = sorted(span_lines(span.within, lines))
        printed = (span.text or "").splitlines()
        if span.numbered:
            numbers = {int(match[1]) for line in printed if (match := NUMBERED_LINE.match(line))}
            return {within[n - 1] for n in numbers if 0 < n <= len(within)}
        # grep prints in the file's order, so each printed line is the next line of the span with its text.
        placed, at = set(), 0
        for text in printed:
            found = next((i for i in range(at, len(within)) if lines[within[i] - 1] == text), None)
            if found is not None:
                placed.add(within[found])
                at = found + 1
        return placed
    if span is None:
        return set()
    (first, last), size = span, len(lines)
    return set(range(max(size + first + 1 if first < 0 else first, 1), min(last or size, size) + 1))


def slashed(path: str) -> str:
    return path.replace("\\", "/")


def absolute(path: str, cwd: str) -> str:
    """A path with forward slashes, made absolute against the call's working directory."""
    path = slashed(path)
    if not (path.startswith("/") or re.match(r"[A-Za-z]:/", path)):
        path = slashed(cwd).rstrip("/") + "/" + path
    return posixpath.normpath(path)


def repo_relative(path: str, root: str, trees: set[str] | tuple[str, ...] = ()) -> str | None:
    """A read's path inside the checkout, a worktree under .claude/worktrees, a sibling fork or a tree a
    tools/quality-sets run read, else None."""
    root = posixpath.normpath(slashed(root))
    windows = re.match(r"[A-Za-z]:/", root) is not None
    for tree in trees:
        if (path.lower() if windows else path).startswith((tree.lower() if windows else tree) + "/"):
            return path[len(tree) + 1:]
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
                parts = PLACEHOLDER.split(re.sub(r"^(?:\./)+", "", mention))
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


def doc_rows(docs: dict[str, str], reads: dict[tuple[str, str], int], opens: list[tuple[str, str, Span]],
             root: str, trees: set[str] | tuple[str, ...] = ()) -> list[dict]:
    """One row per tracked document: who names it, how often a main session and a subagent opened it, how many
    contexts opened it, and how many of those read more than half of its lines."""
    entry = {doc for doc in docs if ENTRY.fullmatch(doc)}
    from_entry = set().union(*(named_in(doc, docs[doc], docs) for doc in entry))
    from_claude_md = named_in("CLAUDE.md", docs.get("CLAUDE.md", ""), docs)
    opened: dict[tuple[str, str], int] = {}
    for (path, who), count in reads.items():
        if (doc := repo_relative(path, root, trees)) in docs:
            opened[doc, who] = opened.get((doc, who), 0) + count
    covered: dict[tuple[str, str], set[int]] = {}
    split: dict[str, list[str]] = {}
    for path, context, span in opens:
        if (doc := repo_relative(path, root, trees)) not in docs:
            continue
        lines = split.setdefault(doc, docs[doc].splitlines())
        covered.setdefault((doc, context), set()).update(span_lines(span, lines))
    contexts: dict[str, int] = {}
    over_half: dict[str, int] = {}
    for (doc, _), lines in covered.items():
        contexts[doc] = contexts.get(doc, 0) + 1
        if 2 * len(lines) > len(docs[doc].splitlines()):
            over_half[doc] = over_half.get(doc, 0) + 1
    rows = []
    for doc in docs:
        if doc == "CLAUDE.md":
            continue
        link = LINKS[0] if doc in entry else LINKS[1] if doc in from_entry else \
            LINKS[2] if doc in from_claude_md else LINKS[3]
        main, agents = opened.get((doc, MAIN), 0), opened.get((doc, SUBAGENT), 0)
        rows.append({"doc": doc, "named_by": link, "reads": main + agents, "main": main, "subagents": agents,
                     "contexts": contexts.get(doc, 0), "over_half": over_half.get(doc, 0)})
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


def runner_command(project: Path) -> str | None:
    """The command a transcript directory's sessions are counted under when it is named for the worktree of a
    tools/quality-sets run, as the system temp directory is spelt either before or after resolving it."""
    temp = tempfile.gettempdir()
    for prefix in {project_name(temp), project_name(str(Path(temp).resolve()))}:
        for command, tree in RUNNERS:
            if re.fullmatch(re.escape(prefix) + tree, project.name):
                return command
    return None


def default_projects(cwd: Path) -> list[Path]:
    """The main checkout's transcript directory, then each worktree's, under .claude/worktrees or a sibling fork,
    then each tools/quality-sets run's."""
    main = default_project(Path(WORKTREE.sub("", str(cwd.resolve()))))
    runs = sorted(p for p in main.parent.glob("*") if runner_command(p)) if main.parent.is_dir() else []
    return [main, *sorted(main.parent.glob(main.name + "-*")), *runs]


MODEL_ROWS = ("command", "agent", "model", "contexts", "calls", "mean_context", "input", "cache_write", "cache_read",
              "output", "input_equivalent", "woken_equivalent", "woken_write")
ROWS = tuple(c for c in MODEL_ROWS if c != "model")
DOCS = ("doc", "named_by", "reads", "main", "subagents", "contexts", "over_half")
RESULTS = ("command", "tool", "results", "characters", "approx_tokens")
SPAWNS = ("command", "agent", "started", "calls", "input_equivalent", "description")


def render(rows: list[dict], columns: tuple[str, ...] = ROWS) -> str:
    cells = [list(columns)] + [[f"{r[c]:,}" if isinstance(r[c], int) else r[c] for c in columns] for r in rows]
    widths = [max(len(row[i]) for row in cells) for i in range(len(columns))]
    left = {"command", "agent", "model", "started", "description", "tool", "doc", "named_by"}
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
    views.add_argument("--models", action="store_true",
                       help="split each command and agent row by the model that answered")
    views.add_argument("--docs", action="store_true",
                       help="list every tracked markdown file with who names it, how often it was opened and how "
                            "many contexts read more than half of it")
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
        report.read_project(project, runner_command(project) or NO_COMMAND)
    print(f"read {projects[0]}" + (f" and {len(projects) - 1} more" if len(projects) > 1 else ""), file=sys.stderr)
    if args.docs:
        root = Path(WORKTREE.sub("", str(Path.cwd().resolve())))
        try:
            docs = tracked_docs(root)
        except (OSError, subprocess.CalledProcessError):
            print(f"no git checkout at {root} to list the markdown files of", file=sys.stderr)
            return 2
        rows, columns = doc_rows(docs, report.reads, report.kept_opens(), str(root), report.trees), DOCS
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
        rows, columns = report.rows(args.models), MODEL_ROWS if args.models else ROWS
    print(json.dumps(rows, indent=2) if args.json else render(rows, columns))
    if report.span:
        print(f"responses from {report.span[0]} to {report.span[1]}", file=sys.stderr)
    if report.skipped:
        print(f"{report.skipped} unreadable transcript lines skipped", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
