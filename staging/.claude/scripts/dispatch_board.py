"""The task list as /dispatch reads and writes it, and the one command line of /dispatch's helpers.

`board` and `tasks move|done|back|place`, each write re-reading the list as it writes, are here; the run's commands
are dispatch_run.py's, reached from here so one grant covers them all. Exit 2 is a refusal: nothing was written.
"""

import argparse
import contextlib
import hashlib
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field
from datetime import date
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
CHILD_TIMEOUT = 120


def __getattr__(name):
    # The executor's result contract is dispatch_run's; /ship's suite reads it from here, the command it is given.
    if name == "RESULT_KEYS":
        import dispatch_run
        return dispatch_run.RESULT_KEYS
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")


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


READ_COPY = "tasks.read.md"


def _note(text):
    return text[:text.find("\n## ")] if "\n## " in text else text


def rewrite(live, read, rebuilt):
    """The session's rebuild of the list, refused unless it starts from what `board` read, keeps the owner's note,
    parses into groups and leaves In progress as it was: those rows change only through move, done and back."""
    if read is None:
        raise Refusal(f"no {READ_COPY} under the run; run board --run first and rebuild from what it read")
    if live != read:
        raise Refusal("the task list changed since board read it; run board --run again and rebuild from that")
    cr = _cr(_lines(live))
    rebuilt = rebuilt.replace("\r\n", "\n").replace("\n", cr + "\n")
    if _note(rebuilt).replace("\r", "") != _note(live).replace("\r", ""):
        raise Refusal("the rebuild changes the note above the sections, which is the owner's")
    before, after = parse_tasks(live), parse_tasks(rebuilt)
    groups(after)
    # Another session's row is never the dispatcher's to drop or edit (spec section 4), whatever state.json holds.
    if sorted(r.line for r in before.in_progress) != sorted(r.line for r in after.in_progress):
        raise Refusal("the rebuild changes the In progress section; its rows change only through move, done and back")
    return rebuilt


def _tasks_command(args, today):
    # The run's state.json is dispatch_run's, which imports this module, so it is reached at call time.
    from dispatch_run import _close, _field, _note_placed, _record_move, _texts, load_state, run_dir
    directory = run_dir(args.root, args.run)
    # Only a run `start` opened is written to, so a mistyped name leaves no phantom state.json behind.
    state = load_state(directory)
    after, undo = [], []

    def update(text):
        if args.action == "rewrite":
            copy = directory / READ_COPY
            try:
                source = _read(args.source)
            except OSError as error:
                raise Refusal(f"could not read {args.source}: {error}") from None
            return rewrite(text, _read(copy) if copy.is_file() else None, source)
        tasks = parse_tasks(text)
        if args.action == "place":
            after.append(lambda: _note_placed(directory, args.issue))
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
        # Another session's row may carry the same letter, so only the keys this run's move recorded are touched.
        entry = state.get("groups", {}).get(args.group)
        if not isinstance(entry, dict) or "rows" not in entry:
            raise Refusal(f"{directory / 'state.json'} does not record {args.group}; it was not moved by this run")
        keys = set(_field(entry, args.group, "keys", _texts))
        rows = _field(entry, args.group, "rows", _texts)
        if sorted(keys) != sorted(_row("Remaining", line).key for line in rows):
            raise Refusal(f"{args.group}'s keys and rows in state.json do not name the same issues")
        status = entry.get("status")
        held = {r.key for r in tasks.in_progress if r.group == args.group}
        if not keys <= held:
            raise Refusal(f"In progress does not hold {args.group}'s rows {', '.join(sorted(keys - held))}")
        if args.action == "done":
            # A row is deleted only once GitHub showed the merge; anything less would hide a claim never checked.
            if status != "verified":
                raise Refusal(f"{args.group} is {status or 'not launched'}, not a verified merge")
            after.append(lambda: _close(directory, args.group, "done"))
            return delete_in_progress(text, keys)
        if status in ("verified", "merged-claimed"):
            raise Refusal(f"{args.group} is {status}: a claimed merge is verified and done, never sent back")
        # The In progress row no longer holds the wave, labels, depends, model or conflicts, so `back` needs these.
        after.append(lambda: _close(directory, args.group, "returned"))
        return return_to_remaining(text, rows, keys)

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


# The helpers both halves read GitHub and git through

def one_line(text, limit=300):
    # Executor and GitHub text reaches one printed line the dispatcher reads; a line break could forge another.
    text = " ".join(str(text).split())
    return text if len(text) <= limit else text[:limit - 1] + "…"


def _number(value):
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


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


def path_which(name):
    """The first executable `name` on PATH, never one in the current directory, or None.

    shutil.which looks in the current directory first on Windows, and the checkout is writable by any merged change,
    so a planted gh.cmd or claude.exe would run in place of the real one. The search is done here, PATH entries only.
    """
    here = os.path.normcase(os.path.realpath(os.getcwd()))
    if os.name == "nt":
        exts = [e for e in os.environ.get("PATHEXT", ".COM;.EXE;.BAT;.CMD").split(os.pathsep) if e]
        names = [name] if os.path.splitext(name)[1].upper() in (e.upper() for e in exts) else [name + e for e in exts]
    else:
        names = [name]
    for directory in os.environ.get("PATH", "").split(os.pathsep):
        if directory in ("", ".") or os.path.normcase(os.path.realpath(directory)) == here:
            continue
        for candidate in names:
            path = os.path.join(directory, candidate)
            if os.path.isfile(path) and os.access(path, os.X_OK):
                real = os.path.normcase(os.path.realpath(path))
                if real != here and not real.startswith(here + os.sep):
                    return path
    return None


def git_bash():
    return _no_relay(_bash_beside(path_which("git") or "") if os.name == "nt" else path_which("bash"))


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
    exe = path_which("git")
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


# Command line

def main(argv=None, body_source=None, open_source=None, branch_source=None, weekly_source=None):
    import dispatch_run
    parser = argparse.ArgumentParser(prog="dispatch_board.py")
    sub = parser.add_subparsers(dest="command", required=True)
    b = sub.add_parser("board")
    b.add_argument("--tasks", required=True)
    b.add_argument("--today")
    b.add_argument("--run")
    t = sub.add_parser("tasks")
    actions = t.add_subparsers(dest="action", required=True)
    for action in ("move", "done", "back", "place", "rewrite"):
        s = actions.add_parser(action)
        s.add_argument("--tasks", required=True)
        s.add_argument("--run", required=True)
        s.add_argument("--today")
        if action == "rewrite":
            s.add_argument("--from", dest="source", required=True)
        elif action == "place":
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
    dispatch_run.add_commands(sub)
    # The task actions resolve their run under the root too, so each takes it after its own name.
    for command in [c for name, c in sub.choices.items() if name != "tasks"] + list(actions.choices.values()):
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
            # Only a run `start` opened takes the copy and the bodies, so a mistyped --run creates nothing.
            directory = dispatch_run.run_dir(root, args.run) if args.run else None
            if directory is not None:
                dispatch_run.load_state(directory)
            text = _read(args.tasks)
            bodies = _bodies(parse_tasks(text), directory, bodies_from)
            print("\n".join(board(text, today, bodies, set(open_from()), branch_from)))
            if directory is not None:
                # What this board read is the only base `tasks rewrite` accepts a rebuild from.
                _write(directory / READ_COPY, text)
        elif args.command == "tasks":
            _tasks_command(args, today)
        else:
            return dispatch_run.command(args, today, bodies_from, open_from, branch_from, weekly_source)
    except Refusal as refusal:
        print(f"refused: {refusal}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    # Executor and GitHub text is UTF-8; a piped stdout on Windows would otherwise be cp1252 and raise on it.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    # dispatch_run imports this file as dispatch_board; running that copy keeps one Refusal class, not two.
    import dispatch_board
    sys.exit(dispatch_board.main())
