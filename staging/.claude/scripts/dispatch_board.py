"""The task list and the night's run as /dispatch reads and writes them. Exit 2 is a refusal: nothing was written.

`board` and `tasks move|done|back|place` are the task list's half, each write re-reading the file as it writes;
`start`, `launch`, `run`, `result`, `verify`, `wait`, `wake` and `resume` the run's, under artifacts/dispatch/<run>/.
"""

import argparse
import contextlib
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import zoneinfo
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

SECTIONS = ("In progress", "Remaining", "Tracking", "Paused")
WAVE_COLUMNS = ("Wave", "Group", "#", "Task", "Labels", "Depends on", "Model", "Parallel with / conflicts")
HEADERS = {
    "In progress": ("Group", "#", "What", "Branch or PR", "State"),
    "Remaining": WAVE_COLUMNS,
    "Tracking": WAVE_COLUMNS,
    "Paused": ("#", "Task", "Labels", "Why paused", "Resume when"),
}
# Where the task and the issue number sit in each section's row.
TASK_AT = {"In progress": 2, "Remaining": 3, "Tracking": 3, "Paused": 1}
NUMBER_AT = {"In progress": 1, "Remaining": 2, "Tracking": 2, "Paused": 0}
WAVE, GROUP, TASK, LABELS, DEPENDS, MODEL, CONFLICTS = 0, 1, 3, 4, 5, 6, 7
EXTENSIONS = "cs|csproj|py|sh|md|json|ya?ml|sql|ps1|props|slnx|toml"
BARE_PATH = re.compile(rf"(?<![\w./\\-])([\w.\\/-]+\.(?:{EXTENSIONS}))(?!\w)", re.IGNORECASE)
KNOWN_FILE = re.compile(rf"\.(?:{EXTENSIONS})$", re.IGNORECASE)
CLAUDE_TREE = re.compile(r"(^|[^\w])\.claude([/\\]|\b)", re.IGNORECASE)
# An issue in Depends on is written #n, after n, waits on n or n's; a bare number is a count, not an issue.
ISSUE_REF = re.compile(
    r"(?:#|\b(?:after|waits on|wait on|waiting on|blocked by)\s+#?)(\d{2,6})\b(?!\s*(?:day|week|hour|month|min))"
    r"|\b(\d{2,6})'s\b", re.IGNORECASE)
DATE = re.compile(r"\d{4}-\d{2}-\d{2}")
NUMBER = re.compile(r"(?<![\w-])#?(\d{2,6})(?![\w-])")
GROUP_ID = re.compile(r"\b[A-Z]\d+\b")
SEPARATOR = re.compile(r"\|[\s:|-]+\|")
OWED = "owed (not filed)"
BRANCH_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._/()-]*")

RUN_NAME = re.compile(r"\d{8}-\d{6}")
LANG = re.compile(r"[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})?")
MODELS = {"Opus": "opus", "Sonnet": "sonnet"}
# tools/quality-sets/quality_sets.py owns these four counts of a `claude -p` envelope's usage; a test holds them equal.
TOKEN_KEYS = ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens")
OUTCOMES = ("merged", "handed-back", "stopped")
REVIEW_ENDS = ("clean", "comments-rule", "plan", "ceiling")
ROUND_KEYS = ("kind", "raised", "fixed", "refused")
ROUND_KINDS = ("full", "recheck")
RESULT_KEYS = ("group", "issues", "outcome", "pr", "merge_oid", "rounds", "review_end", "filed", "reason",
               "workspace", "decisions")
RESULT_CAP = 1 << 20
OID = re.compile(r"[0-9a-f]{40}")
SESSION_ID = re.compile(r"[A-Za-z0-9-]{1,80}")
# A group holds an executor slot while it runs or waits to run again; a week's limit ends the night until it resets.
ACTIVE = ("running", "relaunch", "window", "week")
WINDOW = timedelta(hours=5, minutes=10)
# No limit was hit in the spike (artifacts/dispatch/spike.md, Rate-limit text), so the envelope's structured
# fields are read first and these phrases only on a session that failed; they await the first real limit.
LIMIT_PHRASES = ("usage limit", "rate limit", "limit reached")
RATE_LIMIT_KIND = re.compile(r"rate_limit(?:_error|_exceeded)?", re.IGNORECASE)
ISO_STAMP = r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?"
# A time counts as the reset only right after a reset phrase; any other date in the text is about something else.
RESET_PATTERN = re.compile(
    rf"\breset(?:s)?\s+(?:at\s+|on\s+)?(?:(?P<iso>{ISO_STAMP})|(?P<hour>\d{{1,2}})(?![\d-])(?::(?P<minute>\d{{2}}))?"
    r"\s*(?P<half>[ap]\.?m\b\.?)?(?:\s*\((?P<zone>[A-Za-z_]+(?:/[A-Za-z_+-]+)+)\))?)", re.IGNORECASE)
RESET_KEYS = ("resetsAt", "resets_at", "resetAt", "reset_at")
STILL_ACTIVE = 259
ERROR_ACCESS_DENIED = 5
CHILD_TIMEOUT = 120
# The executor's command, resolved from PATH when None; a suite points it at a fake, never through the environment.
CLAUDE_COMMAND = None
PROJECTS = Path.home() / ".claude" / "projects"
SLEEP = time.sleep


class Refusal(Exception):
    pass


@dataclass(frozen=True)
class Row:
    section: str
    wave: int | None
    group: str | None
    number: int | None
    key: str
    cells: tuple[str, ...]
    line: str


@dataclass
class Tasks:
    in_progress: list[Row] = field(default_factory=list)
    remaining: list[Row] = field(default_factory=list)
    tracking: list[Row] = field(default_factory=list)
    paused: list[Row] = field(default_factory=list)


@dataclass(frozen=True)
class Group:
    id: str
    wave: int
    rows: tuple[Row, ...]
    model: str


@dataclass(frozen=True)
class Claim:
    paths: frozenset[str]
    groups: frozenset[str]
    issues: frozenset[int]
    unknown: bool
    # The group letter a running group's In progress row still carries, so a conflicts cell naming it holds.
    ident: str = ""


# Parsing

def _lines(text):
    # Splitting on the newline alone keeps a CRLF file's carriage returns on their lines, so a write round-trips.
    return text.split("\n")


def _cr(lines):
    return "\r" if lines and lines[0].endswith("\r") else ""


def _cells(line):
    body = line.strip()
    if not (body.startswith("|") and body.endswith("|")) or len(body) < 2:
        raise Refusal(f"not a table row: {body[:60]}")
    return tuple(cell.strip() for cell in re.split(r"(?<!\\)\|", body[1:-1]))


def _scan(lines):
    """Each section's name -> (heading index, body start, body end), refusing a missing, extra or misordered one."""
    heads = [(i, line.rstrip("\r")[3:].strip()) for i, line in enumerate(lines) if line.startswith("## ")]
    names = [name for _, name in heads]
    for name in names:
        if name not in SECTIONS:
            raise Refusal(f"unexpected section {name!r}; the task list has {', '.join(SECTIONS)}")
    for name in SECTIONS:
        if name not in names:
            raise Refusal(f"section {name!r} is missing")
    if names != list(SECTIONS):
        raise Refusal(f"sections are {', '.join(names)}, not {', '.join(SECTIONS)} in that order")
    ends = [i for i, _ in heads[1:]] + [len(lines)]
    return {name: (i, i + 1, end) for (i, name), end in zip(heads, ends)}


def _table(lines, scan, name):
    """(header index, separator index, row indexes, bare-none index) of one section."""
    _, start, end = scan[name]
    table, none = [], None
    for i in range(start, end):
        stripped = lines[i].strip()
        if not stripped:
            continue
        if stripped.startswith("|"):
            table.append(i)
        elif stripped == "none":
            none = i
        else:
            raise Refusal(f"{name} holds text that is neither a table nor none: {stripped[:60]}")
    if none is not None:
        if table:
            raise Refusal(f"{name} holds both a table and none")
        return None, None, [], none
    if len(table) < 2:
        raise Refusal(f"{name} has no table")
    if _cells(lines[table[0]]) != HEADERS[name]:
        raise Refusal(f"{name} header is {' | '.join(_cells(lines[table[0]]))}, not {' | '.join(HEADERS[name])}")
    if not SEPARATOR.fullmatch(lines[table[1]].strip()):
        raise Refusal(f"{name} header is not followed by a separator row")
    return table[0], table[1], table[2:], None


def _row(section, line):
    line = line.rstrip("\r")
    cells = _cells(line)
    if len(cells) != len(HEADERS[section]):
        raise Refusal(f"{section} row has {len(cells)} cells, the header {len(HEADERS[section])}: {line[:60]}")
    first = re.fullmatch(r"#?(\d+)", cells[NUMBER_AT[section]])
    if first:
        number, key = int(first.group(1)), f"#{int(first.group(1))}"
    elif "owed" in cells[NUMBER_AT[section]].casefold():
        number = None
        key = "owed:" + hashlib.sha1(cells[TASK_AT[section]].encode("utf-8")).hexdigest()[:8]
    else:
        raise Refusal(f"{section} row names no issue number and is not owed: {line[:60]}")
    wave = group = None
    if section in ("Remaining", "Tracking"):
        if not re.fullmatch(r"\d+", cells[WAVE]):
            raise Refusal(f"{key} has wave {cells[WAVE]!r}, not a number")
        wave, group = int(cells[WAVE]), cells[GROUP]
    elif section == "In progress":
        # The group it carried in Remaining, kept so a conflicts cell naming it still sees it; a dash is none.
        if cells[0] != "—" and not re.fullmatch(GROUP_ID.pattern, cells[0]):
            raise Refusal(f"{key} has Group {cells[0]!r}, not a group id or a dash")
        group = None if cells[0] == "—" else cells[0]
    return Row(section, wave, group, number, key, cells, line)


def parse_tasks(text):
    lines = _lines(text)
    scan = _scan(lines)
    tasks = Tasks()
    seen = {}
    for name, bucket in zip(SECTIONS, (tasks.in_progress, tasks.remaining, tasks.tracking, tasks.paused)):
        for i in _table(lines, scan, name)[2]:
            row = _row(name, lines[i])
            if row.key in seen:
                raise Refusal(f"{row.key} is in {seen[row.key]} and {name}; a row lives in one table only")
            seen[row.key] = name
            bucket.append(row)
    return tasks


def groups(tasks):
    """The Remaining groups in table order; a group never spans a wave or a model."""
    order, members = [], {}
    for row in tasks.remaining:
        if row.group in ("", "—", "-"):
            raise Refusal(f"{row.key} is in Remaining with no group")
        if row.group not in members:
            order.append(row.group)
        members.setdefault(row.group, []).append(row)
    result = []
    for gid in order:
        rows = members[gid]
        waves = sorted({r.wave for r in rows})
        models = sorted({r.cells[MODEL] for r in rows})
        if len(waves) > 1:
            raise Refusal(f"{gid} spans waves {' and '.join(map(str, waves))}")
        if len(models) > 1:
            raise Refusal(f"{gid} spans models {' and '.join(models)}")
        result.append(Group(gid, waves[0], tuple(rows), models[0]))
    return result


# Paths and claims

def _normalise(token):
    token = token.strip().lstrip("(['\"<").rstrip(".,;:)]>'\"").replace("\\", "/").lower()
    token = re.sub(r":\d+(?:-\d+)?$", "", token)
    while token.startswith("./"):
        token = token[2:]
    return token.rstrip("/")


def extract_paths(text):
    found = set()
    for quoted in re.findall(r"`([^`]+)`", text):
        for piece in quoted.split():
            path = _normalise(piece)
            # A slash command or a dotted identifier is not a file: it would make an empty claim look known.
            if KNOWN_FILE.search(path) or ("/" in path and not path.startswith("/")):
                found.add(path)
    found.update(_normalise(m) for m in BARE_PATH.findall(text))
    found.discard("")
    return frozenset(found)


def _numbers(text):
    return {int(n) for n in NUMBER.findall(DATE.sub(" ", text))}


def group_claim(group, bodies):
    paths, named, issues, unknown = set(), set(), set(), False
    own = {r.number for r in group.rows}
    for row in group.rows:
        paths |= extract_paths(f"{row.cells[TASK]} {row.cells[CONFLICTS]}")
        named |= set(GROUP_ID.findall(row.cells[CONFLICTS])) - {group.id}
        issues |= _numbers(row.cells[CONFLICTS]) - own
        if row.number is not None:
            if row.number in bodies:
                paths |= extract_paths(bodies[row.number])
            else:
                unknown = True
    return Claim(frozenset(paths), frozenset(named), frozenset(issues), unknown or not paths)


def row_branch(row):
    """The branch an In progress row's Branch or PR cell names first in backticks, or None."""
    quoted = re.search(r"`([^`]+)`", row.cells[HEADERS["In progress"].index("Branch or PR")])
    token = quoted.group(1).split()[0] if quoted and quoted.group(1).split() else ""
    return token if BRANCH_NAME.fullmatch(token) else None


def in_progress_claim(row, bodies, branch_paths=frozenset()):
    # Another session's row often names no file; what its branch already changed is the best claim there is.
    paths = set(extract_paths(row.cells[TASK_AT["In progress"]])) | set(branch_paths)
    unknown = False
    if row.number is not None:
        if row.number in bodies:
            paths |= extract_paths(bodies[row.number])
        else:
            unknown = True
    issues = frozenset() if row.number is None else frozenset({row.number})
    ident = row.group or ""
    return Claim(frozenset(paths), frozenset({ident} - {""}), issues, unknown or not paths, ident)


def _touch(a, b):
    if a == b:
        return True
    if "/" in a and "/" in b:
        return a.startswith(b + "/") or b.startswith(a + "/")
    if "/" in b:
        return b.endswith("/" + a)
    return "/" in a and a.endswith("/" + b)


def overlaps(a, a_id, a_issues, b, b_id, b_issues):
    """Whether two claims may not run together: when in doubt they may not."""
    if a.unknown or b.unknown:
        return True
    if a_id in b.groups or b_id in a.groups or a.issues & b_issues or b.issues & a_issues:
        return True
    return any(_touch(p, q) for p in a.paths for q in b.paths)


# Readiness and choice

def _blocker(row, open_issues, today, bodies):
    if row.number is None:
        return "owed, not filed"
    labels = {label.strip() for label in row.cells[LABELS].split(",")}
    if "question" in labels or re.search(r"\bOWNER\b", f"{row.cells[TASK]} {row.cells[DEPENDS]}"):
        return "waits on a human"
    for label in ("paused", "tracking"):
        if label in labels:
            return f"labelled {label}"
    depends = row.cells[DEPENDS]
    if depends.strip().casefold() != "none":
        dates = DATE.findall(depends)
        numbers = {int(a or b) for a, b in ISSUE_REF.findall(DATE.sub(" ", depends))}
        stray = re.search(r"\d", ISSUE_REF.sub(" ", DATE.sub(" ", depends)))
        if (not dates and not numbers) or stray:
            # Free text is never judged here; step 0 turns a human wait into OWNER.
            return "depends on text the helper cannot judge"
        blocking = sorted(n for n in numbers if n in open_issues)
        if blocking:
            return "depends on open " + ", ".join(f"#{n}" for n in blocking)
        for text in dates:
            try:
                due = date.fromisoformat(text)
            except ValueError:
                return f"depends on an impossible date {text}"
            if due > today:
                return f"waits until {text}"
    reach = f"{row.cells[TASK]} {row.cells[CONFLICTS]} {bodies.get(row.number, '')}"
    if CLAUDE_TREE.search(reach.replace("\\", "/")):
        return "edits .claude/"
    return None


def ready(group, open_issues, today, bodies=None):
    for row in group.rows:
        why = _blocker(row, open_issues, today, bodies or {})
        if why:
            return False, why
    return True, ""


def _own_issues(group):
    return {r.number for r in group.rows if r.number is not None}


def pick(candidates, ready_map, claims, running, taken, slots):
    """Up to `slots` group ids to launch. `candidates` is every group, running ones included, so a running
    group's issues are known; the earliest wave goes first and table order breaks a tie."""
    held = [(claims[g.id], g.id, _own_issues(g)) for g in candidates if g.id in running and g.id in claims]
    held += [(claim, claim.ident, set(claim.issues)) for claim in taken]
    unknown = Claim(frozenset(), frozenset(), frozenset(), True)
    chosen = []
    order = sorted(range(len(candidates)), key=lambda i: (candidates[i].wave, i))
    for i in order:
        group = candidates[i]
        if len(chosen) >= slots:
            break
        if group.id in running or not ready_map.get(group.id, (False, ""))[0]:
            continue
        claim, issues = claims.get(group.id, unknown), _own_issues(group)
        if any(overlaps(claim, group.id, issues, other, oid, oissues) for other, oid, oissues in held):
            continue
        chosen.append(group.id)
        held.append((claim, group.id, issues))
    return chosen


# Writes

def _line(cells, cr):
    return "| " + " | ".join(cells) + " |" + cr


def _remove(lines, name, wanted):
    """Remove the section's rows whose key is in `wanted`; the table becomes a bare none if it empties."""
    scan = _scan(lines)
    head, sep, rows, _ = _table(lines, scan, name)
    keyed = {_row(name, lines[i]).key: i for i in rows}
    missing = sorted(wanted - keyed.keys())
    if missing:
        raise Refusal(f"{', '.join(missing)} is not in {name}")
    for i in sorted((keyed[k] for k in wanted), reverse=True):
        del lines[i]
    if wanted and len(rows) == len(wanted):
        lines[head:sep + 1] = ["none" + _cr(lines)]


def _append(lines, name, new):
    scan = _scan(lines)
    head, sep, rows, none = _table(lines, scan, name)
    if none is not None:
        lines[none:none + 1] = [_line(HEADERS[name], _cr(lines)), "|" + "---|" * len(HEADERS[name]) + _cr(lines)] + new
    else:
        at = (rows[-1] if rows else sep) + 1
        lines[at:at] = new


def _place(lines, new_line):
    """Insert a Remaining row after its group, else after its wave, else before the first later wave."""
    scan = _scan(lines)
    _, sep, rows, _ = _table(lines, scan, "Remaining")
    row = _row("Remaining", new_line)
    parsed = [(i, _row("Remaining", lines[i])) for i in rows]
    same_group = [i for i, r in parsed if r.group == row.group]
    same_wave = [i for i, r in parsed if r.wave == row.wave]
    later = [i for i, r in parsed if r.wave > row.wave]
    if same_group:
        at = same_group[-1] + 1
    elif same_wave:
        at = same_wave[-1] + 1
    elif later:
        at = later[0]
    else:
        at = (rows[-1] if rows else sep) + 1
    lines.insert(at, new_line)


def move_to_in_progress(text, group, branch, worktree, today):
    lines = _lines(text)
    cr = _cr(lines)
    _, _, rows, _ = _table(lines, _scan(lines), "Remaining")
    present = {lines[i].rstrip("\r") for i in rows}
    for row in group.rows:
        if row.line not in present:
            raise Refusal(f"{row.key} is not in Remaining as {group.id} expects it")
    new = [_line((group.id, f"#{r.number}" if r.number is not None else OWED, r.cells[TASK],
                  f"`{branch}`, `{worktree}`", f"dispatched {today.isoformat()}"), cr)
           for r in group.rows]
    _remove(lines, "Remaining", {r.key for r in group.rows})
    _append(lines, "In progress", new)
    return "\n".join(lines)


def delete_in_progress(text, keys):
    lines = _lines(text)
    _remove(lines, "In progress", set(keys))
    return "\n".join(lines)


def _owner_marked(cells):
    cells = list(cells)
    depends = cells[DEPENDS]
    if not re.search(r"\bOWNER\b", depends):
        cells[DEPENDS] = "OWNER" if depends.strip().casefold() == "none" else f"OWNER; {depends}"
    return cells


def return_to_remaining(text, rows, keys):
    """Put a stopped group's rows back in their wave, marked OWNER so no later pass relaunches them unseen."""
    lines = _lines(text)
    cr = _cr(lines)
    returning = [_row("Remaining", r) for r in rows]
    taken = {r.key for name in ("Remaining", "Tracking", "Paused") for r in
             (_row(name, lines[i]) for i in _table(lines, _scan(lines), name)[2])}
    clash = sorted(r.key for r in returning if r.key in taken)
    if clash:
        raise Refusal(f"{', '.join(clash)} is already in a table; a row lives in one table only")
    _remove(lines, "In progress", set(keys))
    for row in returning:
        _place(lines, _line(_owner_marked(row.cells), cr))
    return "\n".join(lines)


def place(text, wave, group, issue, task=None, labels=None, depends=None, model=None, conflicts=None):
    """Add a filed issue's row to Remaining. Anything the caller leaves out is the cautious value: the row
    waits on OWNER until someone fills it in."""
    tasks = parse_tasks(text)
    key = f"#{issue}"
    for section in (tasks.in_progress, tasks.remaining, tasks.tracking, tasks.paused):
        if any(r.key == key for r in section):
            raise Refusal(f"{key} already has a row; a row lives in one table only")
    if any(r.group == group for r in tasks.in_progress):
        raise Refusal(f"{group} is held by an In progress row; a re-cut never reuses its letter and number")
    existing = next((g for g in groups(tasks) if g.id == group), None)
    cells = (str(wave), group, str(issue), task or "Filed by /dispatch; read the issue", labels or "code",
             depends or "OWNER", model or (existing.model if existing else "Opus"), conflicts or "none")
    lines = _lines(text)
    _place(lines, _line(cells, _cr(lines)))
    result = "\n".join(lines)
    groups(parse_tasks(result))
    return result


# Command line

def _read(path):
    with open(path, encoding="utf-8", newline="") as handle:
        return handle.read()


def _write(path, text):
    temporary = f"{path}.{os.getpid()}.tmp"
    try:
        with open(temporary, "w", encoding="utf-8", newline="") as handle:
            handle.write(text)
        os.replace(temporary, path)
    except OSError as error:
        # Windows refuses a replace while another process holds the file open.
        with contextlib.suppress(OSError):
            os.remove(temporary)
        raise Refusal(f"could not write {path}: {error}") from None


def _bodies(tasks, run, fetch):
    numbers = sorted({r.number for r in tasks.remaining + tasks.in_progress if r.number is not None})
    cache = Path(run) / "bodies" if run else None
    found = {}
    for n in numbers:
        cached = cache / f"{n}.txt" if cache else None
        if cached and cached.is_file():
            found[n] = _read(cached)
            continue
        found[n] = fetch(n)
        if cached:
            cached.parent.mkdir(parents=True, exist_ok=True)
            _write(cached, found[n])
    return found


def _held(tasks, bodies, branch_source):
    claims = []
    for row in tasks.in_progress:
        branch = row_branch(row)
        claims.append((in_progress_claim(row, bodies, branch_source(branch) if branch else frozenset()), row))
    return claims


def board(text, today, bodies, open_issues, branch_source=lambda _branch: frozenset()):
    tasks = parse_tasks(text)
    held = _held(tasks, bodies, branch_source)
    out = []
    for group in groups(tasks):
        claim = group_claim(group, bodies)
        ok, why = ready(group, open_issues, today, bodies)
        by = next((c.ident or r.key for c, r in held
                   if overlaps(claim, group.id, _own_issues(group), c, c.ident or r.key, set(c.issues))), None)
        out.append(" | ".join((group.id, f"wave {group.wave}", "ready" if ok else f"not ready: {why}",
                               "free" if by is None else f"taken by {by}", f"{len(claim.paths)} paths")))
    return out or ["no groups in Remaining"]


def _state_or_empty(directory):
    return load_state(directory) if (Path(directory) / "state.json").is_file() else {"groups": {}}


def _record_move(directory, gid, moved):
    """Record a move before the list is written, and return how to undo the record if that write is refused."""
    path = Path(directory) / "state.json"
    before = _read(path) if path.is_file() else None
    state = _state_or_empty(directory)
    # A new move starts the group afresh: what an earlier launch recorded no longer describes it.
    state.setdefault("groups", {})[gid] = moved
    save_state(directory, state)

    def undo():
        with contextlib.suppress(OSError, Refusal):
            if before is None:
                path.unlink(missing_ok=True)
            else:
                _write(path, before)
    return undo


def _close(directory, gid, status):
    state = _state_or_empty(directory)
    entry = state.get("groups", {}).get(gid)
    if entry is not None:
        entry["status"] = status
        save_state(directory, state)


def _tasks_command(args, today):
    directory = Path(args.run)
    after, undo = [], []

    def update(text):
        tasks = parse_tasks(text)
        if args.action == "place":
            return place(text, args.wave, args.group, args.issue, args.task, args.labels, args.depends,
                         args.model, args.conflicts)
        if args.action == "move":
            group = next((g for g in groups(tasks) if g.id == args.group), None)
            if group is None:
                raise Refusal(f"{args.group} is not a group in Remaining")
            slug = args.group.lower()
            result = move_to_in_progress(text, group, args.branch or f"chore/dispatch-{slug}",
                                         args.worktree or f".claude/worktrees/dispatch-{slug}", today)
            moved = {"keys": [r.key for r in group.rows], "rows": [r.line for r in group.rows]}
            undo.append(_record_move(directory, args.group, moved))
            return result
        held = {r.key for r in tasks.in_progress if r.group == args.group}
        if not held:
            raise Refusal(f"no In progress row carries Group {args.group}")
        if args.action == "done":
            after.append(lambda: _close(directory, args.group, "done"))
            return delete_in_progress(text, held)
        # The In progress row no longer holds the wave, labels, depends, model or conflicts, so `back` needs these.
        entry = _state_or_empty(directory).get("groups", {}).get(args.group) or {}
        if "rows" not in entry:
            raise Refusal(f"{directory / 'state.json'} does not record {args.group}; it was not moved by this run")
        rows = [line for line in entry["rows"] if _row("Remaining", line).key in held]
        unrecorded = held - {_row("Remaining", line).key for line in rows}
        if unrecorded:
            raise Refusal(f"{directory / 'state.json'} does not hold {', '.join(sorted(unrecorded))}; "
                          "they cannot go back")
        after.append(lambda: _close(directory, args.group, "returned"))
        return return_to_remaining(text, rows, held)

    # The file is read here, at the moment of the write, so another session's edit since `board` is kept. A move's
    # record is written first and undone if the list write is refused, so neither is left without the other.
    text = update(_read(args.tasks))
    try:
        _write(args.tasks, text)
    except Refusal:
        for step in undo:
            step()
        raise
    for step in after:
        step()


# The run: state, children and the helpers it reads GitHub through

def now():
    return datetime.now().astimezone()


def one_line(text, limit=300):
    # Executor and GitHub text reaches one printed line the dispatcher reads; a line break could forge another.
    text = " ".join(str(text).split())
    return text if len(text) <= limit else text[:limit - 1] + "…"


def run_dir(root, run):
    if not isinstance(run, str) or not RUN_NAME.fullmatch(run):
        raise Refusal(f"run name {run!r} is not a yyyymmdd-hhmmss timestamp")
    return Path(root) / "artifacts" / "dispatch" / run


def load_state(directory):
    path = Path(directory) / "state.json"
    try:
        state = json.loads(_read(path))
    except FileNotFoundError:
        raise Refusal(f"{path} is missing; the run was never started") from None
    except ValueError as error:
        raise Refusal(f"{path} is not JSON: {error}") from None
    if not isinstance(state, dict) or not isinstance(state.get("groups", {}), dict):
        raise Refusal(f"{path} is not a run's state")
    return state


def save_state(directory, state):
    directory = Path(directory)
    try:
        directory.mkdir(parents=True, exist_ok=True)
    except OSError as error:
        raise Refusal(f"could not create {directory}: {error}") from None
    _write(directory / "state.json", json.dumps(state, indent=2, ensure_ascii=False) + "\n")


def _start_values(pairs, lang):
    if not 1 <= pairs <= 4:
        raise Refusal(f"--pairs {pairs} is outside 1 to 4")
    if not LANG.fullmatch(lang):
        raise Refusal(f"--lang {lang!r} is not a language code")


def new_run(root, pairs, lang, now):
    _start_values(pairs, lang)
    name = now.strftime("%Y%m%d-%H%M%S")
    directory = run_dir(root, name)
    if directory.exists():
        raise Refusal(f"{directory} already exists")
    save_state(directory, {"run": name, "pairs": pairs, "lang": lang, "started": now.isoformat(), "groups": {}})
    return name


def open_run(root):
    base = Path(root) / "artifacts" / "dispatch"
    if not base.is_dir():
        return None
    names = sorted(p.name for p in base.iterdir() if p.is_dir() and RUN_NAME.fullmatch(p.name)
                   and (p / "state.json").is_file() and not (p / "report.md").exists())
    return names[-1] if names else None


def _entry(state, gid):
    entry = state.get("groups", {}).get(gid)
    if entry is None:
        raise Refusal(f"{gid} is not in this run")
    if not isinstance(entry, dict):
        raise Refusal(f"{gid}'s record in state.json is not an object")
    return entry


def _path_text(value):
    return isinstance(value, str) and bool(value.strip())


def _texts(value):
    return isinstance(value, list) and all(isinstance(item, str) for item in value)


def _numbers_list(value):
    return isinstance(value, list) and all(_number(item) for item in value)


def _field(entry, gid, key, check, default=None):
    """One recorded field of a group, refused rather than a traceback when state.json was edited out of shape."""
    value = entry.get(key, default)
    if not check(value):
        raise Refusal(f"{gid}'s {key} in state.json is missing or malformed")
    return value


def _entry_group(gid, entry):
    rows = tuple(_row("Remaining", line) for line in _field(entry, gid, "rows", _texts, []))
    if not rows:
        raise Refusal(f"{gid} was not moved by this run; tasks move records its rows first")
    models = sorted({r.cells[MODEL] for r in rows})
    if len(models) != 1:
        raise Refusal(f"{gid} spans models {' and '.join(models)}")
    return Group(gid, rows[0].wave, rows, models[0])


def _bash_beside(git):
    """Git for Windows' bin/bash.exe for the git at `git`, walking up its install; None if there is none."""
    here = Path(git).resolve().parent
    for folder in (here, *here.parents)[:3]:
        candidate = folder / "bin" / "bash.exe"
        if candidate.is_file():
            return str(candidate)
    return None


def _no_relay(found):
    # From Python a bare `bash` on Windows is System32's WSL relay, which runs none of this repository's helpers.
    if not found:
        raise Refusal("no Git bash found beside the git on PATH")
    if "system32" in found.replace("/", "\\").casefold():
        raise Refusal(f"{found} is under System32, the WSL relay, not Git's bash")
    return found


def git_bash():
    return _no_relay(_bash_beside(shutil.which("git") or "") if os.name == "nt" else shutil.which("bash"))


def _child(argv, cwd):
    # A hung gh or git would stall the wake the dispatcher is waiting on; an executor never runs through here.
    try:
        done = subprocess.run(argv, cwd=cwd, capture_output=True, timeout=CHILD_TIMEOUT)
    except subprocess.TimeoutExpired:
        raise Refusal(f"{' '.join(Path(a).name if i == 0 else a for i, a in enumerate(argv))} "
                      f"took over {CHILD_TIMEOUT} s") from None
    except OSError as error:
        raise Refusal(f"could not start {Path(argv[0]).name}: {error}") from None
    # Never text=True: on Windows that is cp1252, which drops an em dash or Cyrillic from the output.
    return done.returncode, done.stdout.decode("utf-8", errors="replace"), done.stderr.decode("utf-8", errors="replace")


def helper(root, script, *args):
    """A repository helper's stdout, run from the main checkout through Git's bash, as the grants name it."""
    code, out, err = _child([git_bash(), f".claude/scripts/{script}", *args], root)
    if code:
        raise Refusal(f"{' '.join((script, *args))} exited {code}: {one_line(err or out, 200)}")
    return out


def git(root, *args):
    exe = shutil.which("git")
    if not exe:
        raise Refusal("no git on PATH")
    code, out, err = _child([exe, *args], root)
    if code:
        raise Refusal(f"git {' '.join(args)} exited {code}: {one_line(err, 200)}")
    return out


def branch_paths(root, branch):
    """What a branch has changed since it left origin/main, local first, then its remote copy; none if neither."""
    if not branch or not BRANCH_NAME.fullmatch(branch):
        return frozenset()
    for ref in (branch, f"origin/{branch}"):
        try:
            out = git(root, "diff", "--name-only", f"origin/main...{ref}", "--")
        except Refusal:
            continue
        return frozenset(path for path in (_normalise(p) for p in out.splitlines()) if path)
    return frozenset()


def issue_body(root, number):
    try:
        data = json.loads(helper(root, "gh-issue-text.sh", str(number)))
    except ValueError:
        raise Refusal(f"gh-issue-text.sh {number} did not print JSON") from None
    if not isinstance(data, dict):
        raise Refusal(f"gh-issue-text.sh {number} did not print an issue")
    return f"{data.get('title') or ''}\n{data.get('body') or ''}"


def open_issues(root):
    try:
        data = json.loads(helper(root, "gh-issue-list.sh"))
    except ValueError:
        raise Refusal("gh-issue-list.sh did not print JSON") from None
    if not isinstance(data, list):
        raise Refusal("gh-issue-list.sh did not print a list")
    return {item["number"] for item in data
            if isinstance(item, dict) and item.get("state") == "OPEN" and _number(item.get("number"))}


# Launch

def model_flag(model):
    if model not in MODELS:
        raise Refusal(f"model {model!r} is not one of {', '.join(MODELS)}")
    return MODELS[model]


def prompt(group, description, run):
    run_dir(".", run)
    if any(row.number is None for row in group.rows):
        raise Refusal(f"{group.id} holds an owed row; only filed issues are dispatched")
    if "".join(description.splitlines()) != description:
        raise Refusal("the description holds a line break")
    for token in ("--dispatched", "--sleep"):
        if token in description.casefold():
            raise Refusal(f"the description carries {token}, which only the dispatcher passes")
    if not description.strip():
        raise Refusal("the description is empty")
    tag = f"#{group.id}-" + "+".join(str(row.number) for row in group.rows)
    return f"/ship {tag} {description.strip()} --dispatched artifacts/dispatch/{run}"


def _same_path(path):
    return os.path.normcase(os.path.realpath(path))


def _worktree_branches(porcelain):
    found, current = {}, None
    for line in porcelain.splitlines():
        if line.startswith("worktree "):
            current = _same_path(line[len("worktree "):])
        elif line.startswith("branch ") and current:
            found[current] = line[len("branch "):]
        elif not line:
            current = None
    return found


def launch(root, run, gid, tasks_path, branch, worktree):
    directory = run_dir(root, run)
    state = load_state(directory)
    entry = _entry(state, gid)
    group = _entry_group(gid, entry)
    model_flag(group.model)
    if any(row.number is None for row in group.rows):
        raise Refusal(f"{gid} holds an owed row; only filed issues are dispatched")
    if not BRANCH_NAME.fullmatch(branch):
        raise Refusal(f"{branch!r} is not a branch name")
    keys = _field(entry, gid, "keys", _texts)
    held = [r for r in parse_tasks(_read(tasks_path)).in_progress if r.group == gid]
    if sorted(r.key for r in held) != sorted(keys):
        raise Refusal(f"In progress does not hold {gid}'s rows {', '.join(keys)}")
    named = sorted({row_branch(r) or "none" for r in held})
    if named != [branch]:
        raise Refusal(f"{gid}'s In progress rows name {', '.join(f'`{n}`' for n in named)}, not {branch}")
    # The fork is the dispatcher's own step, so the run trusts only what git shows: a worktree, on this branch.
    listed = _worktree_branches(git(root, "worktree", "list", "--porcelain"))
    if listed.get(_same_path(os.path.join(root, worktree))) != f"refs/heads/{branch}":
        raise Refusal(f"{worktree} is not a worktree on {branch}")
    entry.update(status="running", model=group.model, issues=[r.number for r in group.rows], branch=branch,
                 worktree=worktree, launched=now().isoformat(), crashes=0)
    save_state(directory, state)


def run_session(root, run, gid, description):
    """The dispatcher's background task for one executor, returning when the session ends.

    It writes only its own <group>.pid and <group>.session.json, never state.json, which only the dispatcher writes,
    so parallel executors never race one record.
    """
    directory = run_dir(root, run)
    entry = _entry(load_state(directory), gid)
    if entry.get("status") not in ACTIVE:
        raise Refusal(f"{gid} is {entry.get('status') or 'not launched'}, not waiting for an executor")
    text = prompt(_entry_group(gid, entry), description, run)
    flag = model_flag(entry.get("model"))
    worktree = Path(root) / _field(entry, gid, "worktree", _path_text)
    if not worktree.is_dir():
        raise Refusal(f"{entry['worktree']} is not a directory")
    command = list(CLAUDE_COMMAND) if CLAUDE_COMMAND else [shutil.which("claude")]
    if not command[0]:
        raise Refusal("no claude on PATH")
    for stale in (worktree / "artifacts" / "dispatch" / run / f"{gid}.json", directory / f"{gid}.json",
                  directory / f"{gid}.session.json"):
        try:
            stale.unlink(missing_ok=True)
        except OSError as error:
            raise Refusal(f"could not remove the stale {stale}: {error}") from None
    started = now()
    # The permission mode is the settings' own, never a flag here (spec section 6.2); the prompt goes on stdin.
    try:
        child = subprocess.Popen([*command, "-p", "--output-format", "json", "--model", flag], cwd=worktree,
                                 stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except OSError as error:
        raise Refusal(f"could not start claude: {error}") from None
    try:
        _write(directory / f"{gid}.pid", f"{child.pid}\n")
    except Refusal:
        # An executor resume cannot see would be relaunched over; Popen.kill is the intended stop here.
        child.kill()
        child.wait()
        raise
    out, err = child.communicate(text.encode("utf-8"))
    ended = now()
    record = {"exit": child.returncode, "started": started.isoformat(), "ended": ended.isoformat(),
              "seconds": round((ended - started).total_seconds()),
              "stdout": out.decode("utf-8", errors="replace"), "stderr": err.decode("utf-8", errors="replace")}
    _write(directory / f"{gid}.session.json", json.dumps(record, ensure_ascii=False, indent=1) + "\n")


# What an executor returns (spec section 6.3)

@dataclass(frozen=True)
class Result:
    group: str
    issues: tuple[int, ...]
    outcome: str
    pr: int | None
    merge_oid: str | None
    rounds: tuple[dict, ...]
    review_end: str | None
    filed: tuple[int, ...]
    reason: str | None
    workspace: dict
    decisions: tuple[str, ...]


@dataclass(frozen=True)
class Invalid:
    why: str


def _number(value):
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def _count(value):
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _oid(value):
    return isinstance(value, str) and OID.fullmatch(value) is not None


def _result_problem(data, group, issues):
    if not isinstance(data, dict):
        return "not a JSON object"
    unknown = sorted(set(data) - set(RESULT_KEYS))
    missing = [key for key in RESULT_KEYS if key not in data]
    if unknown or missing:
        return f"keys: unknown {', '.join(unknown) or 'none'}; missing {', '.join(missing) or 'none'}"
    if data["group"] != group:
        return f"group {data['group']!r} is not {group}"
    given = data["issues"]
    if not isinstance(given, list) or not all(_number(n) for n in given) or sorted(given) != sorted(issues):
        return f"issues {given!r} are not {sorted(issues)}"
    outcome, review_end, pr, oid, reason = (data[k] for k in ("outcome", "review_end", "pr", "merge_oid", "reason"))
    if outcome not in OUTCOMES:
        return f"outcome {outcome!r} is not one of {', '.join(OUTCOMES)}"
    if review_end not in REVIEW_ENDS and not (review_end is None and outcome != "merged"):
        return f"review_end {review_end!r} is not one of {', '.join(REVIEW_ENDS)}"
    if outcome == "merged" and not (_number(pr) and _oid(oid)):
        return "a merged result needs its pr number and the whole 40-character merge_oid"
    if (pr is not None and not _number(pr)) or (oid is not None and not _oid(oid)):
        return "pr or merge_oid is neither null nor a pr number and a whole oid"
    if outcome != "merged" and not (isinstance(reason, str) and reason.strip()):
        return f"a {outcome} result needs a reason"
    if reason is not None and not isinstance(reason, str):
        return "reason is not text"
    rounds = data["rounds"]
    if not isinstance(rounds, list):
        return "rounds is not a list"
    for n, item in enumerate(rounds, 1):
        if not (isinstance(item, dict) and sorted(item) == sorted(ROUND_KEYS) and item["kind"] in ROUND_KINDS
                and all(_count(item[key]) for key in ROUND_KEYS[1:])):
            return f"round {n} is not full or recheck with raised, fixed and refused counts"
    if not isinstance(data["filed"], list) or not all(_number(n) for n in data["filed"]):
        return "filed is not a list of issue numbers"
    space = data["workspace"]
    if not (isinstance(space, dict) and sorted(space) == ["removed", "worktree"]
            and isinstance(space["worktree"], str) and isinstance(space["removed"], bool)):
        return "workspace is not a worktree and whether it was removed"
    if not isinstance(data["decisions"], list) or not all(isinstance(d, str) for d in data["decisions"]):
        return "decisions is not a list of text"
    return None


def validate_result(data, group, issues):
    """The executor's result checked as untrusted data: any doubt is Invalid, which the run treats as a crash."""
    why = _result_problem(data, group, issues)
    if why:
        return Invalid(why)
    return Result(data["group"], tuple(data["issues"]), data["outcome"], data["pr"], data["merge_oid"],
                  tuple(data["rounds"]), data["review_end"], tuple(data["filed"]), data["reason"],
                  data["workspace"], tuple(data["decisions"]))


def load_result(path, group, issues):
    """None when the executor wrote nothing; otherwise its result or why it is not one."""
    try:
        if Path(path).stat().st_size > RESULT_CAP:
            return Invalid(f"larger than {RESULT_CAP} bytes")
        raw = Path(path).read_bytes()
    except FileNotFoundError:
        return None
    try:
        data = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError, RecursionError):
        return Invalid("not UTF-8 JSON")
    return validate_result(data, group, issues)


# How the session ended, and what the dispatcher does next (spec section 6.4)

@dataclass(frozen=True)
class SessionEnd:
    exit_code: int
    is_error: bool
    rate_limited: bool
    resets_at: datetime | None
    usage: dict[str, int]
    denials: list[str]
    session_id: str | None = None


def _envelope(stdout):
    try:
        value = json.loads(stdout)
    except (ValueError, RecursionError):
        return None
    return value if isinstance(value, dict) else None


def _fields(value, depth=0):
    """(key, value) for every field of an envelope at any depth, but its free-text result, which anyone steers."""
    if depth > 20:
        return
    if isinstance(value, dict):
        for key, item in value.items():
            if depth == 0 and key == "result":
                continue
            yield key, item
            yield from _fields(item, depth + 1)
    elif isinstance(value, list):
        for item in value:
            yield None, item
            yield from _fields(item, depth + 1)


def _parse_stamp(value):
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        seconds = value / 1000 if value > 1e11 else value
        try:
            return datetime.fromtimestamp(seconds, tz=timezone.utc)
        except (OverflowError, OSError, ValueError):
            return None
    if not isinstance(value, str):
        return None
    try:
        stamp = datetime.fromisoformat(value.strip())
    except ValueError:
        return None
    return (stamp if stamp.tzinfo else stamp.astimezone()).astimezone(timezone.utc)


def _zone(name):
    if not name:
        return None
    try:
        return zoneinfo.ZoneInfo(name)
    except (KeyError, ValueError, OSError):
        # Windows has no zone database without the tzdata package; local time is the fallback.
        return None


def _text_reset(text, base):
    """The reset a limit notice names after a reset phrase: an ISO time after the session ended, else the next
    such clock time."""
    for match in RESET_PATTERN.finditer(text):
        if match["iso"]:
            stamp = _parse_stamp(match["iso"])
            if stamp and stamp > base:
                return stamp
            continue
        hour, minute, half = int(match["hour"]), match["minute"], match["half"]
        if minute is None and half is None:
            continue
        minute = int(minute or 0)
        if half:
            if not 1 <= hour <= 12:
                continue
            hour = hour % 12 + (12 if half[0].lower() == "p" else 0)
        if hour > 23 or minute > 59:
            continue
        local = base.astimezone(_zone(match["zone"]))
        at = local.replace(hour=hour, minute=minute, second=0, microsecond=0)
        if at <= local:
            at += timedelta(days=1)
        return at.astimezone(timezone.utc)
    return None


def session_end(raw):
    code = raw.get("exit")
    code = code if isinstance(code, int) and not isinstance(code, bool) else -1
    stdout = raw.get("stdout") if isinstance(raw.get("stdout"), str) else ""
    stderr = raw.get("stderr") if isinstance(raw.get("stderr"), str) else ""
    envelope = _envelope(stdout)
    flagged = envelope.get("is_error") if envelope else None
    is_error = code != 0 or not isinstance(flagged, bool) or flagged
    spent = envelope.get("usage") if envelope and isinstance(envelope.get("usage"), dict) else {}
    usage = {key: spent[key] for key in TOKEN_KEYS if _count(spent.get(key))}
    listed = envelope.get("permission_denials") if envelope else None
    denials = [one_line(d.get("tool_name") or "unknown" if isinstance(d, dict) else d, 80)
               for d in (listed if isinstance(listed, list) else [])]
    text = envelope["result"] if envelope and isinstance(envelope.get("result"), str) else stdout
    limited, resets = False, None
    if is_error:
        fields = list(_fields(envelope)) if envelope else []
        limited = ((envelope or {}).get("api_error_status") == 429
                   or any(isinstance(v, str) and RATE_LIMIT_KIND.fullmatch(v) for _, v in fields)
                   or re.search(rf"\b{RATE_LIMIT_KIND.pattern}\b", stderr, re.IGNORECASE) is not None
                   or any(phrase in f"{text}\n{stderr}".casefold() for phrase in LIMIT_PHRASES))
        if limited:
            resets = next((s for s in (_parse_stamp(v) for k, v in fields if k in RESET_KEYS) if s), None)
            ended = _parse_stamp(raw.get("ended")) or now()
            resets = resets or _text_reset(f"{text}\n{stderr}", ended)
    session = envelope.get("session_id") if envelope else None
    session = session if isinstance(session, str) and SESSION_ID.fullmatch(session) else None
    return SessionEnd(code, bool(is_error), limited, resets, usage, denials, session)


def classify(end, result, crashes, now):
    if isinstance(result, Result):
        return {"merged": "merged-claimed", "handed-back": "handed-back", "stopped": "stopped"}[result.outcome]
    if end.rate_limited and end.resets_at is not None:
        return "window" if end.resets_at - now <= WINDOW else "week"
    # spike.md, Prompts: a call the auto classifier refused is a hand-back; a relaunch would meet it again.
    if end.denials:
        return "handed-back"
    return "relaunch" if crashes < 1 else "crash-hand-back"


def alive(pid):
    if not _number(pid):
        return False
    if os.name != "nt":
        # Linux is the only other host, CI's; os.kill is never the probe, since on Windows it terminates.
        return Path(f"/proc/{pid}").exists()
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    kernel.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    handle = kernel.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        # A process this user may not query still exists; only a pid with no process behind it is dead.
        return ctypes.get_last_error() == ERROR_ACCESS_DENIED
    try:
        code = wintypes.DWORD()
        return bool(kernel.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == STILL_ACTIVE
    finally:
        kernel.CloseHandle(handle)


def project_slug(path):
    return re.sub(r"[^A-Za-z0-9-]", "-", path)


def _created(path):
    stat = os.stat(path)
    return getattr(stat, "st_birthtime", stat.st_ctime)


def transcript_path(worktree, session_id, since):
    """Where the owner can watch an executor: its session's .jsonl, else the newest one created since its launch."""
    folder = PROJECTS / project_slug(os.path.abspath(worktree))
    if session_id is not None:
        return str(folder / f"{session_id}.jsonl") if SESSION_ID.fullmatch(session_id) else None
    try:
        names = [name for name in os.listdir(folder) if name.endswith(".jsonl")]
        found = [(_created(folder / name), str(folder / name)) for name in names]
    except OSError:
        return None
    found = [item for item in found if item[0] >= since]
    return max(found)[1] if found else None


def _pid(directory, gid):
    try:
        return int(_read(Path(directory) / f"{gid}.pid").strip())
    except (OSError, ValueError):
        return None


def _launch_time(directory, gid, entry):
    try:
        return os.stat(Path(directory) / f"{gid}.pid").st_mtime
    except OSError:
        stamp = _parse_stamp(entry.get("launched"))
        return stamp.timestamp() if stamp else 0.0


def _reason(decision, end, result):
    if isinstance(result, Result):
        return one_line(result.reason) if result.reason and decision != "merged-claimed" else None
    if decision in ("window", "week"):
        return f"resets {end.resets_at.isoformat()}"
    if decision == "handed-back":
        return f"the session was refused {', '.join(sorted(set(end.denials)))} and wrote no result"
    crash = f"{'no result' if result is None else 'invalid result: ' + one_line(result.why, 200)}; exit {end.exit_code}"
    return crash if decision == "relaunch" else f"a second crash: {crash}"


def absorb(root, run, gid):
    """Copy, validate and classify one executor's result and session, record them, and say what happened."""
    directory = run_dir(root, run)
    state = load_state(directory)
    entry = _entry(state, gid)
    if entry.get("status") not in ACTIVE:
        raise Refusal(f"{gid} is {entry.get('status') or 'not launched'}, not waiting on a result")
    record = directory / f"{gid}.session.json"
    try:
        raw = json.loads(_read(record))
    except FileNotFoundError:
        raise Refusal(f"{gid} has no session record; its executor has not exited") from None
    except ValueError:
        raise Refusal(f"{record} is not JSON") from None
    if not isinstance(raw, dict):
        raise Refusal(f"{record} is not a session record")
    if raw.get("started") is not None and raw.get("started") == entry.get("absorbed"):
        raise Refusal(f"{gid}'s session record was already read")
    worktree = _field(entry, gid, "worktree", _path_text)
    source = Path(root) / worktree / "artifacts" / "dispatch" / run / f"{gid}.json"
    copy = directory / f"{gid}.json"
    try:
        if source.is_file():
            shutil.copyfile(source, copy)
        else:
            copy.unlink(missing_ok=True)
    except OSError as error:
        raise Refusal(f"could not copy {source}: {error}") from None
    result = load_result(copy, gid, _field(entry, gid, "issues", _numbers_list))
    end = session_end(raw)
    crashes = _field(entry, gid, "crashes", _count, 0)
    decision = classify(end, result, crashes, now())
    reason = _reason(decision, end, result)
    entry.update(status=decision, absorbed=raw.get("started"), exit=end.exit_code, session_id=end.session_id,
                 usage=end.usage, denials=end.denials, reason=reason)
    if decision in ("relaunch", "crash-hand-back"):
        entry["crashes"] = crashes + 1
    if end.resets_at is not None:
        entry["resets_at"] = end.resets_at.isoformat()
    if isinstance(result, Result):
        entry.update(pr=result.pr, merge_oid=result.merge_oid, filed=list(result.filed),
                     review_end=result.review_end, rounds=list(result.rounds), decisions=list(result.decisions))
    entry["transcript"] = transcript_path(str(Path(root) / worktree), end.session_id,
                                          _launch_time(directory, gid, entry))
    save_state(directory, state)
    shown = entry["transcript"] if entry["transcript"] and os.path.isfile(entry["transcript"]) else "not found"
    parts = [f"{gid}: {decision}"]
    if isinstance(result, Result) and result.pr:
        parts.append(f"pr #{result.pr}")
    if reason:
        parts.append(f"- {reason}")
    if end.denials:
        parts.append(f"[denials: {len(end.denials)}]")
    return [" ".join(parts), f"transcript: {shown}"]


def _merge_problem(root, pr, oid, branch):
    """Where GitHub contradicts the claim, or None: merged, at this oid, from this branch, into main.

    A read that fails or answers out of shape is a Refusal, not a contradiction: the claim stays to be checked again.
    """
    if not _number(pr) or not _oid(oid) or not isinstance(branch, str) or not BRANCH_NAME.fullmatch(branch):
        raise Refusal("the claim in state.json names no pr, whole oid or branch")
    try:
        view = json.loads(helper(root, "pr-state.sh", str(pr)))
        listed = json.loads(helper(root, "pr-for-branch.sh", branch))
    except Refusal as error:
        raise Refusal(f"GitHub could not be read: {error}") from None
    except ValueError:
        raise Refusal("GitHub's answer was not JSON") from None
    if not isinstance(view, dict) or not isinstance(listed, list):
        raise Refusal("GitHub's answer has another shape")
    if view.get("state") != "MERGED":
        return f"#{pr} is {view.get('state')}, not MERGED"
    commit = view.get("mergeCommit")
    merged = commit.get("oid") if isinstance(commit, dict) else None
    if merged != oid:
        return f"#{pr} merged as {merged}, not {oid}"
    mine = next((p for p in listed if isinstance(p, dict) and p.get("number") == pr), None)
    if mine is None:
        return f"#{pr} is not a pull request from {branch}"
    if mine.get("state") != "MERGED" or mine.get("baseRefName") != "main":
        return f"#{pr} from {branch} is {mine.get('state')} into {mine.get('baseRefName')}, not merged into main"
    heads = (mine.get("headRefOid"), view.get("headRefOid"))
    if not all(_oid(head) for head in heads) or heads[0] != heads[1]:
        return f"#{pr}'s head is not {branch}'s"
    return None


def verify(root, run, gid):
    directory = run_dir(root, run)
    state = load_state(directory)
    entry = _entry(state, gid)
    if entry.get("status") != "merged-claimed":
        raise Refusal(f"{gid} is {entry.get('status') or 'not launched'}, not a claimed merge")
    why = _merge_problem(root, entry.get("pr"), entry.get("merge_oid"), entry.get("branch"))
    if why:
        why = one_line(why)
        entry.update(status="stopped", reason=f"merge not verified: {why}")
        save_state(directory, state)
        return 3, f"not verified: {why}"
    entry["status"] = "verified"
    save_state(directory, state)
    return 0, f"verified {entry['merge_oid']}"


def _week_passed(entry, current):
    # A week's reset that cannot be read is taken as not yet come: relaunching into the limit wastes the night.
    stamp = _parse_stamp(entry.get("resets_at"))
    return stamp is not None and stamp < current


def window_wait(resets_at, now):
    if resets_at <= now:
        return 0.0
    return min((resets_at - now).total_seconds() + 60, WINDOW.total_seconds())


def wait(root, run, gid):
    directory = run_dir(root, run)
    entry = _entry(load_state(directory), gid)
    resets = _parse_stamp(entry.get("resets_at"))
    if entry.get("status") != "window" or resets is None:
        raise Refusal(f"{gid} is {entry.get('status') or 'not launched'}, not waiting on a window")
    total = left = window_wait(resets, now())
    # In steps, so the owner's stop file ends a five-hour wait within a minute rather than after it.
    while left > 0:
        if (directory / "STOP").exists():
            return "stop file: yes"
        step = min(60.0, left)
        SLEEP(step)
        left -= step
    return f"waited {total:.0f} s"


def wake(root, run, tasks_path, today, body_source, open_source, branch_source):
    directory = run_dir(root, run)
    state = load_state(directory)
    entries = state.get("groups", {})
    if not all(isinstance(entry, dict) for entry in entries.values()):
        raise Refusal(f"{directory / 'state.json'} holds a group that is not an object")
    current = now()
    active = [gid for gid, entry in entries.items() if entry.get("status") in ACTIVE]
    stop = (directory / "STOP").exists()
    out = [f"stop file: {'yes' if stop else 'no'}", f"running: {', '.join(active) or 'none'}"]
    for gid in active:
        entry = entries[gid]
        worktree = Path(root) / _field(entry, gid, "worktree", _path_text)
        live = (transcript_path(str(worktree), None, _launch_time(directory, gid, entry))
                if entry.get("status") == "running" else entry.get("transcript"))
        out.append(f"transcript {gid}: {live or 'none yet'}")
    pairs = state.get("pairs")
    if not _number(pairs):
        raise Refusal(f"{directory / 'state.json'} has no whole number of pairs")
    slots = max(0, pairs - len(active))
    out.append(f"free slots: {slots}")
    if stop:
        return out + ["end: stop-file"]
    if any(entry.get("status") == "week" and not _week_passed(entry, current) for entry in entries.values()):
        return out + ["end: week"]
    tasks = parse_tasks(_read(tasks_path))
    bodies = _bodies(tasks, directory, body_source)
    open_set = set(open_source())
    candidates = groups(tasks)
    ready_map = {g.id: ready(g, open_set, today, bodies) for g in candidates}
    claims = {g.id: group_claim(g, bodies) for g in candidates}
    taken = [claim for claim, _ in _held(tasks, bodies, branch_source)]
    chosen = pick(candidates, ready_map, claims, [], taken, slots)
    if chosen:
        return out + [f"launch: {' '.join(chosen)}"]
    return out + ["launch: none" if active else "end: drained"]


def resume(root, run):
    """What each of this run's unfinished groups needs next; worktrees the run never recorded are not its own."""
    directory = run_dir(root, run)
    out, current = [], now()
    for gid, entry in load_state(directory).get("groups", {}).items():
        if not isinstance(entry, dict):
            raise Refusal(f"{gid}'s record in state.json is not an object")
        status = entry.get("status")
        if status in ACTIVE:
            pid = _pid(directory, gid)
            if pid is not None and alive(pid):
                out.append(f"refused {gid}: its executor is still running")
                continue
            if not entry.get("worktree") or not (Path(root) / entry["worktree"]).is_dir():
                out.append(f"refused {gid}: its worktree {entry.get('worktree')} is gone")
                continue
            try:
                started = json.loads(_read(directory / f"{gid}.session.json")).get("started")
            except (OSError, ValueError, AttributeError):
                started = None
            if started is not None and started != entry.get("absorbed"):
                out.append(f"result {gid}: its executor ended and its result was not read")
            elif status == "window":
                out.append(f"wait {gid}: the window resets {entry.get('resets_at')}, then relaunch")
            elif status == "week" and not _week_passed(entry, current):
                stamp = _parse_stamp(entry.get("resets_at"))
                out.append(f"wait {gid}: the week resets {stamp.isoformat() if stamp else 'unknown'}")
            else:
                out.append(f"relaunch {gid}")
        elif status is None:
            out.append(f"launch {gid}: moved, never launched")
        elif status == "merged-claimed":
            out.append(f"verify {gid}")
        elif status == "verified":
            out.append(f"done {gid}: verified, its rows are still In progress")
        elif status in ("handed-back", "stopped", "crash-hand-back"):
            out.append(f"back {gid}: {status}")
    return out


# Command line

def main(argv=None, body_source=None, open_source=None, branch_source=None):
    parser = argparse.ArgumentParser(prog="dispatch_board.py")
    sub = parser.add_subparsers(dest="command", required=True)
    b = sub.add_parser("board")
    b.add_argument("--tasks", required=True)
    b.add_argument("--today")
    b.add_argument("--run")
    t = sub.add_parser("tasks")
    actions = t.add_subparsers(dest="action", required=True)
    for action in ("move", "done", "back", "place"):
        s = actions.add_parser(action)
        s.add_argument("--tasks", required=True)
        s.add_argument("--run", required=True)
        s.add_argument("--today")
        if action == "place":
            s.add_argument("wave", type=int)
            s.add_argument("group")
            s.add_argument("issue", type=int)
            for option in ("task", "labels", "depends", "model", "conflicts"):
                s.add_argument(f"--{option}")
        else:
            s.add_argument("group")
        if action == "move":
            s.add_argument("--branch")
            s.add_argument("--worktree")
    s = sub.add_parser("start")
    s.add_argument("--pairs", type=int, required=True)
    s.add_argument("--lang", default="en")
    for name in ("launch", "run", "result", "verify", "wait"):
        s = sub.add_parser(name)
        s.add_argument("run")
        s.add_argument("group")
        if name == "launch":
            for option in ("tasks", "branch", "worktree"):
                s.add_argument(f"--{option}", required=True)
        if name == "run":
            s.add_argument("--description", required=True)
    w = sub.add_parser("wake")
    w.add_argument("run")
    w.add_argument("--tasks", required=True)
    w.add_argument("--today")
    sub.add_parser("resume").add_argument("run")
    for command in sub.choices.values():
        command.add_argument("--root", default=".")
    args = parser.parse_args(argv)
    root = args.root
    bodies_from = body_source or (lambda number: issue_body(root, number))
    open_from = open_source or (lambda: open_issues(root))
    branch_from = branch_source or (lambda branch: branch_paths(root, branch))
    try:
        # A group names files under the run, so only a group letter and number may reach a path.
        if getattr(args, "group", None) is not None and not re.fullmatch(GROUP_ID.pattern, args.group):
            raise Refusal(f"{args.group!r} is not a group id")
        try:
            today = date.fromisoformat(args.today) if getattr(args, "today", None) else date.today()
        except ValueError:
            raise Refusal(f"--today {args.today!r} is not an ISO date") from None
        if args.command == "board":
            text = _read(args.tasks)
            bodies = _bodies(parse_tasks(text), args.run, bodies_from)
            print("\n".join(board(text, today, bodies, set(open_from()), branch_from)))
        elif args.command == "tasks":
            _tasks_command(args, today)
        elif args.command == "start":
            _start_values(args.pairs, args.lang)
            existing = open_run(root)
            if existing:
                print(existing)
                return 3
            print(new_run(root, args.pairs, args.lang, now()))
        elif args.command == "launch":
            launch(root, args.run, args.group, args.tasks, args.branch, args.worktree)
        elif args.command == "run":
            run_session(root, args.run, args.group, args.description)
        elif args.command == "result":
            print("\n".join(absorb(root, args.run, args.group)))
        elif args.command == "verify":
            code, line = verify(root, args.run, args.group)
            print(line)
            return code
        elif args.command == "wait":
            print(wait(root, args.run, args.group))
        elif args.command == "wake":
            print("\n".join(wake(root, args.run, args.tasks, today, bodies_from, open_from, branch_from)))
        else:
            print("\n".join(resume(root, args.run)))
    except Refusal as refusal:
        print(f"refused: {refusal}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    # Executor and GitHub text is UTF-8; a piped stdout on Windows would otherwise be cp1252 and raise on it.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main())
