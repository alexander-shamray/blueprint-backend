"""The task list as /dispatch reads and writes it: its groups, which are ready, which are free, and what runs next.

`board` prints one line per group; `tasks move|done|back|place` are the only writes the dispatcher makes to the task
list, each re-reading the file at the moment it writes. Exit 2 is a refusal: nothing was written.
"""

import argparse
import hashlib
import json
import os
import re
import sys
from dataclasses import dataclass, field
from datetime import date
from pathlib import Path

SECTIONS = ("In progress", "Remaining", "Tracking", "Paused")
WAVE_COLUMNS = ("Wave", "Group", "#", "Task", "Labels", "Depends on", "Model", "Parallel with / conflicts")
HEADERS = {
    "In progress": ("#", "What", "Branch or PR", "State"),
    "Remaining": WAVE_COLUMNS,
    "Tracking": WAVE_COLUMNS,
    "Paused": ("#", "Task", "Labels", "Why paused", "Resume when"),
}
# Where the task and the issue number sit in each section's row.
TASK_AT = {"In progress": 1, "Remaining": 3, "Tracking": 3, "Paused": 1}
WAVE, GROUP, TASK, LABELS, DEPENDS, MODEL, CONFLICTS = 0, 1, 3, 4, 5, 6, 7
EXTENSIONS = "cs|csproj|py|sh|md|json|ya?ml|sql|ps1|props|slnx|toml"
BARE_PATH = re.compile(rf"(?<![\w./\\-])([\w.\\/-]+\.(?:{EXTENSIONS}))(?!\w)", re.IGNORECASE)
DATE = re.compile(r"\d{4}-\d{2}-\d{2}")
NUMBER = re.compile(r"(?<![\w-])#?(\d{2,6})(?![\w-])")
GROUP_ID = re.compile(r"\b[A-Z]\d+\b")
SEPARATOR = re.compile(r"\|[\s:|-]+\|")
OWED = "owed (not filed)"


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
    first = re.fullmatch(r"#?(\d+)", cells[2 if section in ("Remaining", "Tracking") else 0])
    if first:
        number, key = int(first.group(1)), f"#{int(first.group(1))}"
    elif "owed" in cells[2 if section in ("Remaining", "Tracking") else 0].casefold():
        number = None
        key = "owed:" + hashlib.sha1(cells[TASK_AT[section]].encode("utf-8")).hexdigest()[:8]
    else:
        raise Refusal(f"{section} row names no issue number and is not owed: {line[:60]}")
    wave = group = None
    if section in ("Remaining", "Tracking"):
        if not re.fullmatch(r"\d+", cells[WAVE]):
            raise Refusal(f"{key} has wave {cells[WAVE]!r}, not a number")
        wave, group = int(cells[WAVE]), cells[GROUP]
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
            if "/" in path or re.search(r"\.[a-z][a-z0-9]+$", path):
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


def in_progress_claim(row, bodies):
    paths = set(extract_paths(row.cells[1]))
    unknown = False
    if row.number is not None:
        if row.number in bodies:
            paths |= extract_paths(bodies[row.number])
        else:
            unknown = True
    issues = frozenset() if row.number is None else frozenset({row.number})
    return Claim(frozenset(paths), frozenset(), issues, unknown or not paths)


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
        numbers = _numbers(depends)
        if not dates and not numbers:
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
    if any(".claude" in path.split("/") for path in extract_paths(reach)):
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
    held += [(claim, "", set(claim.issues)) for claim in taken]
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
    new = [_line((f"#{r.number}" if r.number is not None else OWED, r.cells[TASK],
                  f"`{branch}`, `{worktree}`", f"dispatched {today.isoformat()}"), cr) for r in group.rows]
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
    with open(temporary, "w", encoding="utf-8", newline="") as handle:
        handle.write(text)
    os.replace(temporary, path)


def _unavailable(*_):
    raise Refusal("bodies and the open set need Task 3's git_bash and gh-issue-text.sh")


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


def board(text, today, bodies, open_issues):
    tasks = parse_tasks(text)
    held = [(in_progress_claim(r, bodies), r) for r in tasks.in_progress]
    out = []
    for group in groups(tasks):
        claim = group_claim(group, bodies)
        ok, why = ready(group, open_issues, today, bodies)
        by = next((r.key for c, r in held
                   if overlaps(claim, group.id, _own_issues(group), c, r.key, set(c.issues))), None)
        out.append(" | ".join((group.id, f"wave {group.wave}", "ready" if ok else f"not ready: {why}",
                               "free" if by is None else f"taken by {by}", f"{len(claim.paths)} paths")))
    return out or ["no groups in Remaining"]


def _sidecar(run, group):
    return Path(run) / "groups" / f"{group}.json"


def _tasks_command(args, today):
    def update(text):
        tasks = parse_tasks(text)
        if args.action == "place":
            return place(text, args.wave, args.group, args.issue, args.task, args.labels, args.depends,
                         args.model, args.conflicts)
        sidecar = _sidecar(args.run, args.group)
        if args.action == "move":
            group = next((g for g in groups(tasks) if g.id == args.group), None)
            if group is None:
                raise Refusal(f"{args.group} is not a group in Remaining")
            slug = args.group.lower()
            result = move_to_in_progress(text, group, args.branch or f"chore/dispatch-{slug}",
                                         args.worktree or f".claude/worktrees/dispatch-{slug}", today)
            sidecar.parent.mkdir(parents=True, exist_ok=True)
            _write(sidecar, json.dumps({"keys": [r.key for r in group.rows], "rows": [r.line for r in group.rows]}))
            return result
        if not sidecar.is_file():
            raise Refusal(f"{sidecar} is missing; {args.group} was not moved by this run")
        recorded = json.loads(_read(sidecar))
        if args.action == "done":
            return delete_in_progress(text, set(recorded["keys"]))
        return return_to_remaining(text, recorded["rows"], set(recorded["keys"]))

    # The file is read here, at the moment of the write, so another session's edit since `board` is kept.
    _write(args.tasks, update(_read(args.tasks)))


def main(argv=None, body_source=_unavailable, open_source=_unavailable):
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
    args = parser.parse_args(argv)
    try:
        try:
            today = date.fromisoformat(args.today) if args.today else date.today()
        except ValueError:
            raise Refusal(f"--today {args.today!r} is not an ISO date") from None
        if args.command == "board":
            text = _read(args.tasks)
            bodies = _bodies(parse_tasks(text), args.run, body_source)
            print("\n".join(board(text, today, bodies, set(open_source()))))
        else:
            _tasks_command(args, today)
    except Refusal as refusal:
        print(f"refused: {refusal}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
