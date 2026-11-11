"""The task list as /dispatch reads and writes it, and the one command line of /dispatch's helpers.

`board`, `tasks move|done|back|place` and a day session's run-less `tasks start|state|done|back <issue>` are here;
the run's commands are dispatch_run.py's, reached from here so one grant covers them all. Exit 2: nothing written.
"""

import argparse
import contextlib
import functools
import hashlib
import json
import os
import re
import signal
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
MODELS = {"Opus": "opus", "Sonnet": "sonnet"}
EXTENSIONS = "cs|csproj|py|sh|md|json|ya?ml|sql|ps1|props|slnx|toml"
BARE_PATH = re.compile(rf"(?<![\w./\\-])([\w.\\/-]+\.(?:{EXTENSIONS}))(?!\w)", re.IGNORECASE)
KNOWN_FILE = re.compile(rf"\.(?:{EXTENSIONS})$", re.IGNORECASE)
CLAUDE_TREE = re.compile(r"(^|[^\w])\.claude([/\\]|\b)", re.IGNORECASE)
# What the Task cell and the body name beneath .claude/: a bare `.claude/` in prose names no file to change.
CLAUDE_PATH = re.compile(r"(^|[^\w])\.claude/[\w.-]", re.IGNORECASE)
# An issue in Depends on is written #n, after n, waits on n or n's; a bare number is a count, not an issue.
ISSUE_REF = re.compile(
    r"(?:#|\b(?:after|waits on|wait on|waiting on|blocked by)\s+#?)(\d{2,6})\b(?!\s*(?:day|week|hour|month|min))"
    r"|\b(\d{2,6})'s\b", re.IGNORECASE)
DATE = re.compile(r"\d{4}-\d{2}-\d{2}")
NUMBER = re.compile(r"(?<![\w-])#?(\d{2,6})(?![\w-])")
OWNER = re.compile(r"\bOWNER\b")
# What `tasks back` adds on a --berserk run: OWNER alone is a question, which the next flagged run would lift.
SENT_BACK = "sent back under --berserk"
# A task-list group. A sync's is a run's alone, since no row ever holds one, so `tasks` takes only this.
ROW_GROUP = re.compile(r"\b[A-Z]\d+\b")
GROUP_ID = re.compile(rf"{ROW_GROUP.pattern}|\bsync-[1-9][0-9]*\b")
SEPARATOR = re.compile(r"\|[\s:|-]+\|")
OWED = "owed (not filed)"
NOT_OWNERS = "not opened by the repository owner"
# The text cells of a row `tasks place` adds, read from a file under the run.
PLACED = ("task", "labels", "depends", "model", "conflicts")
BRANCH_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._/()-]*")
CHILD_TIMEOUT = 120
# How long a timed-out child's output may take to close once its tree is ended.
DRAIN_TIMEOUT = 10
# The checkout this file is in, whose directory names say which backticked slash tokens are paths.
ROOT = Path(__file__).resolve().parents[2]
UNWALKED = {".git", "worktrees", "artifacts", "bin", "obj", "node_modules", "testresults", "__pycache__"}


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
        # A dash or an empty cell is no group, which `groups` reports for Remaining; anything else is a group id.
        if group not in ("", "—", "-") and not re.fullmatch(ROW_GROUP.pattern, group):
            raise Refusal(f"{key} has Group {group!r}, not a group id or a dash")
    elif section == "In progress":
        # The group it carried in Remaining, kept so a conflicts cell naming it still sees it; a dash is none.
        if cells[0] != "—" and not re.fullmatch(ROW_GROUP.pattern, cells[0]):
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


def _raise(error):
    raise error


@functools.cache
def _directory_names():
    """Every directory name in the checkout, lower-cased, or None when it cannot be walked.

    Read from the checkout, not listed here, so a directory added later counts. Copies of the tree and build output
    are named but not walked: their names are the checkout's own again.
    """
    names = set()
    try:
        for _here, dirs, _files in os.walk(ROOT, onerror=_raise):
            names.update(d.lower() for d in dirs)
            dirs[:] = [d for d in dirs if d.lower() not in UNWALKED]
    except OSError:
        return None
    return frozenset(names)


def _slash_path(path):
    # A dot, or a first segment naming a directory anywhere in the checkout, makes a slash token a path and `and/or`
    # not, so `Services/Ordering` still claims (the argument page: the extraction errs towards exclusive).
    if "/" not in path or path.startswith("/"):
        return False
    names = _directory_names()
    return names is None or "." in path or path.split("/", 1)[0] in names


def extract_paths(text):
    found = set()
    for quoted in re.findall(r"`([^`]+)`", text):
        for piece in quoted.split():
            path = _normalise(piece)
            # A slash command or a dotted identifier is not a file: it would make an empty claim look known.
            if KNOWN_FILE.search(path) or _slash_path(path):
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
        named |= set(ROW_GROUP.findall(row.cells[CONFLICTS])) - {group.id}
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

def _without_owner(depends):
    """*Depends on* with its OWNER gone, so the rest reads as it would alone; a cell of OWNER and nothing else is none."""
    rest = OWNER.sub(" ", depends)
    return depends if rest == depends else rest.strip(" ;,:.—–-") or "none"


def model_problem(model):
    return None if model in MODELS else f"model {model!r} is not one of {', '.join(MODELS)}"


def _blocker(row, open_issues, today, bodies, berserk=False):
    """Why a row cannot launch, or None. `berserk` lifts the human wait and the .claude/ reach and nothing else."""
    if row.number is None:
        return "owed, not filed"
    labels = {label.strip() for label in row.cells[LABELS].split(",")}
    if not berserk and ("question" in labels or OWNER.search(f"{row.cells[TASK]} {row.cells[DEPENDS]}")):
        return "waits on a human"
    for label in ("paused", "tracking"):
        if label in labels:
            return f"labelled {label}"
    if berserk and SENT_BACK in row.cells[DEPENDS]:
        return SENT_BACK
    depends = _without_owner(row.cells[DEPENDS]) if berserk else row.cells[DEPENDS]
    if UNPLACED in depends:
        return "unplaced: no session has chosen its wave and group"
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
    # The conflicts cell is a list of paths, so any .claude there is a claim; elsewhere only a path beneath it is.
    prose = f"{row.cells[TASK]} {bodies.get(row.number, '')}".replace("\\", "/")
    if not berserk and (CLAUDE_TREE.search(row.cells[CONFLICTS].replace("\\", "/")) or CLAUDE_PATH.search(prose)):
        return "edits .claude/"
    # Here as well as at `launch`, which runs after the fork and `tasks move`: its refusal leaves an empty worktree.
    return model_problem(row.cells[MODEL])


def ready(group, open_issues, today, bodies=None, berserk=False, kept=frozenset()):
    """Whether every row may launch, and the first that may not. A row in `kept` is one the flag does not lift."""
    for row in group.rows:
        why = _blocker(row, open_issues, today, bodies or {}, berserk and row.key not in kept)
        if why:
            return False, why
    return True, ""


def strangers(group, owned):
    """The key of each row whose issue the repository owner did not open."""
    return [row.key for row in group.rows if row.number is not None and row.number not in owned]


def runnable(group, owned, open_issues, today, bodies=None, berserk=False, kept=frozenset()):
    """`ready`, behind the gate no flag lifts: on a public repository an issue is a work order only from the owner."""
    held = strangers(group, owned)
    if held:
        return False, f"{NOT_OWNERS}: {', '.join(held)}"
    return ready(group, open_issues, today, bodies, berserk, kept)


def released(candidates, ready_map, open_issues, today, bodies):
    """Each group `ready_map` holds ready that would not be without the flag, with what the flag lifted."""
    plain = ((g.id, ready(g, open_issues, today, bodies)) for g in candidates if ready_map[g.id][0])
    return {gid: why for gid, (ok, why) in plain if not ok}


def _own_issues(group):
    return {r.number for r in group.rows if r.number is not None}


def pick(candidates, ready_map, claims, running, taken, slots, lifted=()):
    """Up to `slots` group ids to launch. `candidates` is every group, running ones included, so a running
    group's issues are known; the earliest wave goes first, within it a group in `lifted` after the rest, so a limit
    ends the night on the riskier work, and table order breaks a tie."""
    held = [(claims[g.id], g.id, _own_issues(g)) for g in candidates if g.id in running and g.id in claims]
    held += [(claim, claim.ident, set(claim.issues)) for claim in taken]
    unknown = Claim(frozenset(), frozenset(), frozenset(), True)
    chosen = []
    order = sorted(range(len(candidates)), key=lambda i: (candidates[i].wave, candidates[i].id in lifted, i))
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


def _place(lines, new_line, name="Remaining"):
    """Insert a Remaining or Tracking row after its group, else after its wave, else before the first later wave."""
    scan = _scan(lines)
    _, sep, rows, none = _table(lines, scan, name)
    if none is not None:
        _append(lines, name, [new_line])
        return
    row = _row(name, new_line)
    parsed = [(i, _row(name, lines[i])) for i in rows]
    same_group = [i for i, r in parsed if r.group == row.group and re.fullmatch(ROW_GROUP.pattern, r.group or "")]
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


def _owner_marked(cells, sent_back=False):
    cells = list(cells)
    depends = cells[DEPENDS]
    if sent_back and SENT_BACK not in depends:
        rest = _without_owner(depends)
        cells[DEPENDS] = depends = SENT_BACK if rest.strip().casefold() == "none" else f"{SENT_BACK}; {rest}"
    if not OWNER.search(depends):
        cells[DEPENDS] = "OWNER" if depends.strip().casefold() == "none" else f"OWNER; {depends}"
    return cells


def return_to_remaining(text, rows, keys, sent_back=False):
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
        _place(lines, _line(_owner_marked(row.cells, sent_back), cr))
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


# A day session's moves: one issue's row, keyed by its number, with no run

def _checked(text):
    """The list a day write would leave, refused unless `board`'s parser reads it, so the night never stops on it."""
    try:
        groups(parse_tasks(text))
    except Refusal as refusal:
        raise Refusal(f"the list this write leaves is one board refuses: {refusal}") from None
    return text


def _in_progress_row(tasks, key, branch=None):
    """`key`'s In progress row; for work with no issue (key None), the owed row whose branch is `branch`."""
    if key is None:
        return next((r for r in tasks.in_progress if r.number is None and row_branch(r) == branch), None)
    return next((r for r in tasks.in_progress if r.key == key), None)


def _key(number):
    return None if number is None else f"#{number}"


def _name(number, branch):
    return f"#{number}" if number is not None else f"the owed work on {branch}"


def _a_day_row(row):
    # A row a /dispatch run moved is that run's to end: its state.json records it, and a day write would orphan that.
    if row.cells[HEADERS["In progress"].index("State")].startswith("dispatched"):
        raise Refusal(f"{row.key} was moved by a /dispatch run, which ends it; a day session changes nothing there")
    return row


def _one_line(value, what):
    if not value.strip() or "|" in value or "\n" in value or "\r" in value:
        raise Refusal(f"{what} {value!r} is not one line of text without a pipe")
    return value.strip()


def start_issue(text, number, branch, worktree, what=None):
    """(the list with `#number` in progress, the Remaining line it left), or (None, None) when this branch holds it.

    The row keeps the group it had in Remaining; an issue with no row starts under `—` with `what` as its text, and
    work with no issue (number None) as `owed (not filed)`, found again by its branch."""
    if not BRANCH_NAME.fullmatch(branch):
        raise Refusal(f"branch {branch!r} is not a branch name")
    if "`" in _one_line(worktree, "worktree"):
        raise Refusal(f"worktree {worktree!r} holds a backtick")
    tasks, key = parse_tasks(text), _key(number)
    held = _in_progress_row(tasks, key, branch)
    if held is not None:
        if row_branch(held) == branch:
            return None, None
        raise Refusal(f"{key} is already in progress as {held.cells[3]}; a second branch starts nothing")
    if key is None:
        if what is None:
            raise Refusal("work with no issue takes --what for its In progress row")
        lines = _lines(text)
        _append(lines, "In progress", [_line(("—", OWED, _one_line(what, "--what"),
                                              f"`{branch}`, `{worktree.strip()}`", "started"), _cr(lines))])
        return _checked("\n".join(lines)), None
    for name, bucket in (("Tracking", tasks.tracking), ("Paused", tasks.paused)):
        if any(r.key == key for r in bucket):
            raise Refusal(f"{key} is in {name}; a row moves to In progress only from Remaining")
    source = next((r for r in tasks.remaining if r.key == key), None)
    lines = _lines(text)
    if source is None:
        if what is None:
            raise Refusal(f"{key} has no row in Remaining; give --what for its In progress row")
        group, task = "—", _one_line(what, "--what")
    else:
        group, task = source.group if re.fullmatch(ROW_GROUP.pattern, source.group) else "—", source.cells[TASK]
        _remove(lines, "Remaining", {key})
    _append(lines, "In progress", [_line((group, key, task, f"`{branch}`, `{worktree.strip()}`", "started"),
                                         _cr(lines))])
    return _checked("\n".join(lines)), source.line if source else None


def finish_issue(text, number, branch=None):
    """The list without `#number`'s In progress row, or None when it has none: a merge leaves nothing to undo."""
    held = _in_progress_row(parse_tasks(text), _key(number), branch)
    if held is None:
        return None
    return _checked(delete_in_progress(text, {_a_day_row(held).key}))


def return_issue(text, number, saved):
    """The list with `#number` back where `start` took it from, `saved` being that Remaining line; None when it is
    not in progress. With no saved line, a row written by hand or started with --what, it is refused: an open issue
    goes back to its wave, and nothing here knows the wave or the cells."""
    held = _in_progress_row(parse_tasks(text), f"#{number}")
    if held is None:
        return None
    key = _a_day_row(held).key
    if saved is None:
        raise Refusal(f"{key} has no Remaining line saved by tasks start, so its wave is unknown; it stays In progress")
    lines = _lines(text)
    _remove(lines, "In progress", {key})
    row = _row("Remaining", saved)
    if row.key != key:
        raise Refusal(f"the row saved for {key} is {row.key}'s")
    _place(lines, _line(row.cells, _cr(lines)))
    # A row someone wrote for the issue since its start is a clash `board`'s parser names here.
    return _checked("\n".join(lines))


def set_issue_state(text, number, state, branch=None):
    """The list with only `#number`'s State cell changed, or None when it already reads `state`."""
    state = _one_line(state, "state")
    held = _in_progress_row(parse_tasks(text), _key(number), branch)
    if held is None:
        raise Refusal(f"{_name(number, branch)} has no In progress row; tasks start writes one")
    at = HEADERS["In progress"].index("State")
    if _a_day_row(held).cells[at] == state:
        return None
    lines = _lines(text)
    index = next(i for i in _table(lines, _scan(lines), "In progress")[2] if lines[i].rstrip("\r") == held.line)
    lines[index] = _line(held.cells[:at] + (state,) + held.cells[at + 1:], _cr(lines))
    return _checked("\n".join(lines))


# The events that place, move or delete a row by an issue's live state, and the whole-list rebuild

@dataclass(frozen=True)
class Issue:
    number: int
    title: str
    open: bool
    labels: tuple[str, ...]


# The Depends on cell of a row no session has placed yet: free text, so `board` holds it until one does.
UNPLACED = "unplaced: no session has chosen its wave and group"
# The letter a row the helper adds with no group is given, the number the lowest no table holds.
UNPLACED_LETTER = "U"
PAUSED_WHY = "labelled paused; the issue's pause comment says why"
PAUSED_RESUME = "the paused label is removed"
# The cells a caller may give a row `tasks place` or `tasks sync` adds; everything else comes from the issue.
CALLER_CELLS = ("wave", "group", "task", "depends", "model", "conflicts", "why", "resume")
# Set in a /dispatch executor's environment: the dispatcher is the list's one writer while it runs.
DISPATCHED = "HARNESS_DISPATCHED"


def issues_from(data):
    """{number: Issue} from gh-issue-list.sh's JSON, refusing anything that is not that list."""
    if not isinstance(data, list):
        raise Refusal("the issue list is not a list")
    found = {}
    for item in data:
        if not isinstance(item, dict) or not _number(item.get("number")) or not isinstance(item.get("title"), str):
            raise Refusal("the issue list holds an entry that is not an issue")
        labels = item.get("labels") or []
        if not isinstance(labels, list):
            raise Refusal(f"#{item['number']}'s labels are not a list")
        names = (label.get("name") if isinstance(label, dict) else label for label in labels)
        found[item["number"]] = Issue(item["number"], item["title"], item.get("state") == "OPEN",
                                      tuple(name for name in names if isinstance(name, str) and name))
    return found


def where(issue):
    """The section an issue's row belongs in, or None for a closed one, which has no row."""
    if not issue.open:
        return None
    if "paused" in issue.labels:
        return "Paused"
    return "Tracking" if "tracking" in issue.labels else "Remaining"


def _title_cell(title):
    # A title can hold a pipe, which would split the row; `_cells` reads an escaped one as text.
    text = " ".join(title.split())
    if not text:
        raise Refusal("the issue's title is empty")
    return text.replace("|", "\\|")


def _caller_cells(cells):
    cells = {k: v for k, v in (cells or {}).items() if v is not None}
    unknown = set(cells) - set(CALLER_CELLS)
    if unknown:
        raise Refusal(f"no cell named {', '.join(sorted(unknown))}")
    checked = {k: _one_line(str(v), f"--{k}") for k, v in cells.items()}
    if "wave" in checked and not re.fullmatch(r"\d+", checked["wave"]):
        raise Refusal(f"--wave {checked['wave']!r} is not a number")
    if "group" in checked and not re.fullmatch(ROW_GROUP.pattern, checked["group"]):
        raise Refusal(f"--group {checked['group']!r} is not a group id")
    if "model" in checked and checked["model"] not in MODELS:
        raise Refusal(model_problem(checked["model"]))
    return checked


def _rows(tasks):
    return tasks.in_progress + tasks.remaining + tasks.tracking + tasks.paused


def _fresh_group(tasks):
    held = {r.group for r in _rows(tasks) if r.group}
    k = 1
    while f"{UNPLACED_LETTER}{k}" in held:
        k += 1
    return f"{UNPLACED_LETTER}{k}"


def _cells_for(section, issue, tasks, cells, old=None, owned=frozenset()):
    """The cells of `issue`'s row in `section`: the caller's first, then what the row had in Remaining or Tracking,
    then what the issue says. A row nobody placed waits as unplaced, and a `question` as OWNER. A title the owner did
    not write never reaches the list, which the dispatcher reads: the cell says whose it is instead."""
    labels = ", ".join(issue.labels) or "none"
    title = _title_cell(issue.title) if issue.number in owned else f"Issue #{issue.number}, {NOT_OWNERS}"
    task = cells.get("task") or (old.cells[TASK_AT[old.section]] if old else title)
    if section == "Paused":
        return str(issue.number), task, labels, cells.get("why", PAUSED_WHY), cells.get("resume", PAUSED_RESUME)
    kept = old if old is not None and old.section in ("Remaining", "Tracking") else None
    waves = [r.wave for r in tasks.remaining + tasks.tracking if r.wave is not None]
    wave = cells.get("wave") or (str(kept.wave) if kept else str(max(waves, default=1)))
    group = cells.get("group") or (kept.group if kept else None)
    fresh = section == "Remaining" and not (group and re.fullmatch(ROW_GROUP.pattern, group))
    if fresh:
        group = _fresh_group(tasks)
    # A group the helper made up is no session's placement, so the row waits until one regroups it.
    depends = cells.get("depends") or (UNPLACED if fresh else kept.cells[DEPENDS] if kept else "none")
    if "question" in issue.labels and not OWNER.search(depends):
        depends = "OWNER" if depends.casefold() == "none" else f"OWNER; {depends}"
    existing = next((g for g in groups(tasks) if g.id == group), None) if section == "Remaining" else None
    model = cells.get("model") or (existing.model if existing else None) or (kept.cells[MODEL] if kept else None)
    if section == "Remaining" and model not in MODELS:
        model = "Opus"
    return (wave, group or "—", str(issue.number), task, labels, depends, model or "—",
            cells.get("conflicts") or (kept.cells[CONFLICTS] if kept else "none"))


def _insert(lines, section, cells):
    line = _line(cells, _cr(lines))
    if section == "Paused":
        _append(lines, section, [line])
    else:
        _place(lines, line, section)


def settle(text, issue, cells=None, new=False, owned=frozenset()):
    """(the list with `issue`'s row where its live state puts it, what was done), or (None, why nothing was).

    A closed issue's row is deleted; an open one's added, or moved by its labels. An In progress row is its session's
    to end with `done` or `back`, so nothing here moves it (spec section 4). `new` is a filing: a row there is refused."""
    cells = _caller_cells(cells)
    tasks, key = parse_tasks(text), f"#{issue.number}"
    row = next((r for r in _rows(tasks) if r.key == key), None)
    target = where(issue)
    if new and row is not None:
        raise Refusal(f"{key} already has a row in {row.section}; tasks sync moves it")
    if new and target is None:
        raise Refusal(f"{key} is closed, so it has no row")
    lines = _lines(text)
    if row is not None and row.section == "In progress":
        return None, f"{key} is in progress; its row stays until the work ends with tasks done or back"
    if target is None:
        if row is None:
            return None, f"{key} is closed and has no row"
        _remove(lines, row.section, {key})
        return _checked("\n".join(lines)), f"deleted {key} from {row.section}: the issue is closed"
    if row is not None and row.section == target:
        return None, f"{key} is already in {target}"
    if row is not None:
        _remove(lines, row.section, {key})
        tasks = parse_tasks("\n".join(lines))
    _insert(lines, target, _cells_for(target, issue, tasks, cells, row, owned))
    done = f"moved {key} from {row.section} to {target}" if row else f"added {key} to {target}"
    return _checked("\n".join(lines)), done


def reconcile(text, issues, merged=lambda _branch: False, owned=frozenset()):
    """(the list with every deterministic rule applied, one line per change, one per row left for a person).

    `issues` is every issue, open and closed; `merged(branch)` says whether a branch's pull request merged; `owned`
    the issues the repository owner opened, whose titles alone a new row may carry."""
    notes, kept = [], []
    tasks = parse_tasks(text)
    # In progress is never rebuilt: each row is its session's or its run's to end, so a stale one is only named.
    for row in tasks.in_progress:
        if row.cells[HEADERS["In progress"].index("State")].startswith("dispatched"):
            continue
        branch = row_branch(row)
        if row.number is None:
            if branch and merged(branch):
                kept.append(f"kept the owed row on {branch} in In progress: its pull request merged; "
                            f"tasks done owed --branch {branch} ends it")
        elif row.number not in issues:
            kept.append(f"kept {row.key} in In progress: no such issue here")
        elif not issues[row.number].open:
            kept.append(f"kept {row.key} in In progress: the issue is closed; tasks done {row.number} ends it")
        elif branch and merged(branch):
            kept.append(f"kept {row.key} in In progress: its pull request merged and the issue is open; "
                        "tasks back or close the issue")
    for section in ("Remaining", "Tracking", "Paused"):
        for row in getattr(parse_tasks(text), section.lower().replace(" ", "_")):
            if row.number is None:
                continue
            if row.number not in issues:
                kept.append(f"kept {row.key} in {section}: no such issue here")
                continue
            result, done = settle(text, issues[row.number], owned=owned)
            if result is not None:
                text = result
                notes.append(done)
    present = {r.number for r in _rows(parse_tasks(text))}
    for number in sorted(n for n, issue in issues.items() if issue.open and n not in present):
        text, done = settle(text, issues[number], new=True, owned=owned)
        notes.append(f"{done}{', unplaced' if where(issues[number]) == 'Remaining' else ''}")
    return text, notes, kept


def _same_issues(before, after):
    """Refuse a rewrite that adds, drops or moves an issue's row: GitHub decides those, through `settle` and
    `reconcile`. An owed row has no issue to read, so a rewrite may add, drop or reword one."""
    for name in ("Remaining", "Tracking", "Paused"):
        attr = name.lower()
        was = {r.key for r in getattr(before, attr) if r.number is not None}
        now = {r.key for r in getattr(after, attr) if r.number is not None}
        if was != now:
            raise Refusal(f"the rewrite adds, drops or moves {', '.join(sorted(was ^ now))} in {name}; "
                          "tasks sync and reconcile do that from GitHub")


def rewrite_day(live, rebuilt):
    """A session's regrouping of the list, refused unless each issue's row stays in its section and In progress is
    as it was: a rewrite re-cuts waves, groups and cells, and only `settle` and `reconcile` add, move or delete."""
    cr = _cr(_lines(live))
    rebuilt = rebuilt.replace("\r\n", "\n").replace("\n", cr + "\n")
    before, after = parse_tasks(live), parse_tasks(rebuilt)
    _checked(rebuilt)
    if sorted(r.line for r in before.in_progress) != sorted(r.line for r in after.in_progress):
        raise Refusal("the rewrite changes In progress; its rows change only through start, state, done and back")
    _same_issues(before, after)
    return None if rebuilt == live else rebuilt


# What `tasks init` puts above the sections: where the rules are, never the rules themselves.
PREAMBLE = ("# Task list", "",
            "Written only through `dispatch_board.py tasks`; the shape and the rules are "
            "`docs/harness/task-list.md`.", "")
EMPTY = "\n".join((*PREAMBLE, *(part for name in SECTIONS for part in (f"## {name}", "", "none", "")))).rstrip("\n") \
    + "\n"


def init(text):
    """An empty list, or `text` with whatever sits above its first section replaced by the preamble."""
    if text is None:
        return EMPTY
    lines = _lines(text)
    first = _scan(lines)[SECTIONS[0]][0]
    cr = _cr(lines)
    result = "\n".join([line + cr for line in PREAMBLE] + lines[first:])
    parse_tasks(result)
    return None if result == text else _checked(result)


def _read_or_refuse(path):
    try:
        return _read(path)
    except OSError as error:
        raise Refusal(f"could not read {path}: {error}") from None


# How many times a day write starts again from a list another session changed under it.
SWAP_TRIES = 5


def _swap(path, edit):
    """Write `edit`'s result over the list, unless the file changed since it was read: then the edit starts again
    from the fresh copy. That narrows the window another writer's edit is lost in to the moment between the last
    compare and the replace; it does not close it. False: nothing to do."""
    for _ in range(SWAP_TRIES):
        text = _read_or_refuse(path)
        result = edit(text)
        if result is None:
            return False
        if _read_or_refuse(path) != text:
            continue
        _write(path, result)
        return True
    raise Refusal(f"{path} changed under another writer {SWAP_TRIES} times running; nothing was written")


def task_list(root):
    """The path `.harness/config.json` names under `task_list`, in the main checkout's parent, from any worktree."""
    main = Path(git(root, "rev-parse", "--path-format=absolute", "--git-common-dir").strip()).parent
    config = main / ".harness" / "config.json"
    try:
        name = json.loads(_read(config)).get("task_list")
    except (OSError, ValueError, AttributeError) as error:
        raise Refusal(f"could not read task_list from {config}: {error}") from None
    if not isinstance(name, str) or not name.strip():
        raise Refusal(f"{config} names no task_list")
    return main.parent / name


def _issue(value):
    found = re.fullmatch(r"#?([1-9]\d{0,6})", value)
    if not found:
        raise Refusal(f"{value!r} is not an issue number")
    return int(found.group(1))


def _work(value):
    """An issue number, or None for `owed`: work with no issue, which its branch names instead."""
    return None if value == "owed" else _issue(value)


def _whole_list(args, path, issues_source, merged_source, owned_source):
    """init, reconcile and rewrite: the commands that read or rebuild the whole list rather than one issue's row."""
    if args.action == "init":
        if not path.exists():
            try:
                path.parent.mkdir(parents=True, exist_ok=True)
            except OSError as error:
                raise Refusal(f"could not make {path.parent}: {error}") from None
            _write(path, EMPTY)
            print(f"wrote an empty list at {path}")
            return
        wrote = _swap(path, init)
        print("replaced the text above the sections" if wrote else "the list already opens on the preamble")
        return
    if args.action == "rewrite":
        try:
            source = _read(args.source)
        except OSError as error:
            raise Refusal(f"could not read {args.source}: {error}") from None
        wrote = _swap(path, lambda text: rewrite_day(text, source))
        print("rewrote the list" if wrote else "the rewrite matches the list; nothing written")
        return
    issues, owned, notes = issues_source(), owned_source(), []

    def edit(text):
        result, done, kept = reconcile(text, issues, merged_source, owned)
        notes[:] = done + kept
        return result if result != text else None
    _swap(path, edit)
    print("\n".join(notes) or "the list already matches GitHub; nothing written")


def _day_command(args, issues_source, merged_source, owned_source):
    # The settings base grants every session these prefixes, so the run-less forms write only the list the config
    # names. The same `done`, `back`, `place` and `rewrite` prefixes reach the --run forms, which refuse without the
    # run's state.json.
    if getattr(args, "tasks", None) or args.root != ".":
        raise Refusal(f"tasks {args.action} without --run writes only the list .harness/config.json names; "
                      "it takes no --tasks or --root")
    # Present at all is set: an empty value is still the dispatcher's mark, never a way past it.
    if DISPATCHED in os.environ:
        raise Refusal("a /dispatch executor writes no task list: the dispatcher is its one writer, and places what "
                      "the result's filed names")
    path = task_list(".")
    if args.action in ("init", "reconcile", "rewrite"):
        _whole_list(args, path, issues_source, merged_source, owned_source)
        return
    if args.action in ("place", "sync"):
        number = _issue(args.issue)
        issue = issues_source().get(number)
        if issue is None:
            raise Refusal(f"#{number} is not an issue in this repository")
        cells = {name: getattr(args, name, None) for name in CALLER_CELLS}
        owned, said = owned_source(), []

        def settled(text):
            result, done = settle(text, issue, cells, new=args.action == "place", owned=owned)
            said[:] = [done]
            return result
        wrote = _swap(path, settled)
        print(said[0] if wrote else f"{said[0]}; nothing written")
        return
    number = _work(args.issue)
    branch = getattr(args, "branch", None)
    if number is None:
        if args.action == "back":
            raise Refusal("work with no issue has no wave to go back to; tasks done owed --branch ends its row")
        if not branch:
            raise Refusal(f"tasks {args.action} owed takes --branch, the branch its row names")
        where_ = f"the owed work on {branch}"
        if args.action == "state":
            wrote = _swap(path, lambda text: set_issue_state(text, None, args.text, branch))
            print(f"{where_} reads {args.text.strip()!r}" if wrote else f"{where_} already reads that; nothing written")
        elif args.action == "done":
            wrote = _swap(path, lambda text: finish_issue(text, None, branch))
            print(f"done {where_}" if wrote else f"{where_} has no In progress row; nothing written")
        else:
            wrote = _swap(path, lambda text: start_issue(text, None, branch, args.worktree, args.what)[0])
            print(f"started {where_}" if wrote else f"{where_} is already in progress; nothing written")
        return
    # What `start` took out of Remaining, kept beside the list so `back` puts the whole row back, cells and all.
    saved = path.with_name(f"{path.stem}.started") / f"{number}.md"
    if args.action == "start":
        took = []

        def edit(text):
            result, line = start_issue(text, number, args.branch, args.worktree, args.what)
            if line is not None:
                try:
                    saved.parent.mkdir(parents=True, exist_ok=True)
                except OSError as error:
                    raise Refusal(f"could not make {saved.parent}: {error}") from None
                _write(saved, line)
                took.append(line)
            return result
        try:
            wrote = _swap(path, edit)
        except Refusal:
            # The list was not written, so a row saved on the way would name a start that never happened.
            if took:
                with contextlib.suppress(OSError):
                    saved.unlink()
            raise
        print(f"started #{number}" if wrote else f"#{number} is already in progress on {args.branch}; nothing written")
        return
    if args.action == "state":
        wrote = _swap(path, lambda text: set_issue_state(text, number, args.text))
        print(f"#{number} reads {args.text.strip()!r}" if wrote else f"#{number} already reads that; nothing written")
        return
    if args.action == "done":
        wrote = _swap(path, lambda text: finish_issue(text, number))
    else:
        line = _read_or_refuse(saved).rstrip("\r\n") if saved.is_file() else None
        wrote = _swap(path, lambda text: return_issue(text, number, line))
    if wrote or args.action == "done":
        with contextlib.suppress(FileNotFoundError):
            saved.unlink()
    print(f"{args.action} #{number}" if wrote else f"#{number} has no In progress row; nothing written")


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


def board(text, today, bodies, open_issues, branch_source=lambda _branch: frozenset(), berserk=False,
          kept=frozenset(), *, owned):
    tasks = parse_tasks(text)
    held = _held(tasks, bodies, branch_source)
    out = []
    for group in groups(tasks):
        claim = group_claim(group, bodies)
        ok, why = runnable(group, owned, open_issues, today, bodies, berserk, kept)
        lifted = released([group], {group.id: (ok, why)}, open_issues, today, bodies).get(group.id)
        by = next((c.ident or r.key for c, r in held
                   if overlaps(claim, group.id, _own_issues(group), c, c.ident or r.key, set(c.issues))), None)
        readiness = f"released: {lifted}" if lifted else "ready" if ok else f"not ready: {why}"
        out.append(" | ".join((group.id, f"wave {group.wave}", readiness,
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
    _same_issues(before, after)
    return rebuilt


def _placement(directory, issue):
    """The cells `<run>/place-<n>.json` gives a new row, or none: an issue's text never reaches a shell."""
    path = Path(directory) / f"place-{issue}.json"
    if not path.is_file():
        return {}
    try:
        cells = json.loads(_read(path))
    except (OSError, ValueError) as error:
        raise Refusal(f"could not read {path}: {error}") from None
    if not isinstance(cells, dict) or set(cells) - set(PLACED) or not all(isinstance(v, str) for v in cells.values()):
        raise Refusal(f"{path} is not an object holding {', '.join(PLACED)} as text")
    return cells


def _tasks_command(args, today):
    # The run's state.json is dispatch_run's, which imports this module, so it is reached at call time.
    from dispatch_run import _close, _field, _note_placed, _record_move, _texts, berserk, load_state, run_dir
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
            return place(text, args.wave, args.group, args.issue, **_placement(directory, args.issue))
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
        return return_to_remaining(text, rows, keys, berserk(state)[0])

    # The file is read here, at the moment of the write, so another session's edit since `board` is kept. A move's
    # record is written first and undone if the list write is refused, so neither is left without the other.
    text = update(_read(args.tasks))
    try:
        _write(args.tasks, text)
    except Refusal as refused:
        failed = []
        for step in undo:
            try:
                step()
            except (OSError, Refusal) as error:
                failed.append(str(error))
        if failed:
            # The record then names a move the list never got, so the refusal says so beside its own cause.
            raise Refusal(f"{refused}; and the move's record in state.json was not undone: {'; '.join(failed)}") \
                from None
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
        # A relative entry resolves against the working directory, which the checkout controls.
        if not os.path.isabs(directory) or os.path.normcase(os.path.realpath(directory)) == here:
            continue
        for candidate in names:
            path = os.path.join(directory, candidate)
            if os.path.isfile(path) and os.access(path, os.X_OK):
                real = os.path.normcase(os.path.realpath(path))
                if real != here and not real.startswith(os.path.join(here, "")):
                    return path
    return None


def git_bash():
    return _no_relay(_bash_beside(path_which("git") or "") if os.name == "nt" else path_which("bash"))


@functools.cache
def _kernel32():
    import ctypes
    from ctypes import wintypes
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateJobObjectW.argtypes = (wintypes.LPVOID, wintypes.LPCWSTR)
    kernel32.CreateJobObjectW.restype = wintypes.HANDLE
    kernel32.AssignProcessToJobObject.argtypes = (wintypes.HANDLE, wintypes.HANDLE)
    kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
    kernel32.TerminateJobObject.argtypes = (wintypes.HANDLE, wintypes.UINT)
    kernel32.TerminateJobObject.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
    kernel32.CloseHandle.restype = wintypes.BOOL
    ntdll = ctypes.WinDLL("ntdll")
    ntdll.NtResumeProcess.argtypes = (wintypes.HANDLE,)
    ntdll.NtResumeProcess.restype = wintypes.LONG
    return kernel32, ntdll


# Popen's creationflags for a child that runs nothing until it is resumed.
CREATE_SUSPENDED = 0x00000004


def _start(argv, cwd, env):
    """(process, job): the child, and on Windows the job holding it from its first instruction, so each process it
    starts joins the job whatever its parent. Parentage cannot do this: msys's `exec` leaves its process under a parent
    that has exited, which `taskkill /T` cannot walk past. The job is None off Windows, or where none can be made."""
    pipes = {"cwd": cwd, "stdout": subprocess.PIPE, "stderr": subprocess.PIPE, "env": env}
    if os.name != "nt":
        return subprocess.Popen(argv, start_new_session=True, **pipes), None
    try:
        kernel32, ntdll = _kernel32()
        job = kernel32.CreateJobObjectW(None, None)
    except (OSError, AttributeError):
        job = None
    if not job:
        return subprocess.Popen(argv, **pipes), None
    try:
        process = subprocess.Popen(argv, creationflags=CREATE_SUSPENDED, **pipes)
    except BaseException:
        kernel32.CloseHandle(job)
        raise
    if not kernel32.AssignProcessToJobObject(job, int(process._handle)):
        kernel32.CloseHandle(job)
        job = None
    if ntdll.NtResumeProcess(int(process._handle)) != 0:
        _end_tree(process, job)
        if job:
            kernel32.CloseHandle(job)
        raise OSError(f"could not resume {Path(argv[0]).name} after starting it suspended")
    return process, job


def _end_tree(process, job):
    """End `process` and every process it started: its job on Windows, `taskkill /T` where it has none, and its
    session elsewhere. `kill` alone ends the direct child, which for Git's `bin/bash.exe` is a launcher."""
    if job:
        with contextlib.suppress(OSError):
            _kernel32()[0].TerminateJobObject(job, 1)
    elif os.name == "nt":
        taskkill = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "taskkill.exe")
        with contextlib.suppress(OSError, subprocess.SubprocessError):
            subprocess.run([taskkill, "/F", "/T", "/PID", str(process.pid)], capture_output=True,
                           timeout=DRAIN_TIMEOUT, check=False)
    else:
        with contextlib.suppress(OSError):
            os.killpg(process.pid, signal.SIGKILL)
    with contextlib.suppress(OSError):
        process.kill()


def run_bounded(argv, cwd, timeout, env=None):
    """(code, stdout, stderr) as bytes, or subprocess.TimeoutExpired once `timeout` has passed and the child's tree is
    ended. `subprocess.run`'s own timeout kills the direct child and then, on Windows, waits with no limit for pipes a
    grandchild still holds, so the wait after the kill is bounded here too (docs/harness/pitfalls.md)."""
    process, job = _start(argv, cwd, env)
    try:
        out, err = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        _end_tree(process, job)
        # Output already read is dropped: the caller refuses the read either way.
        with contextlib.suppress(subprocess.TimeoutExpired):
            process.communicate(timeout=DRAIN_TIMEOUT)
        raise subprocess.TimeoutExpired(argv, timeout) from None
    except BaseException:
        # An interrupt leaves the child running otherwise, and on POSIX its own session keeps Ctrl-C from it.
        _end_tree(process, job)
        raise
    finally:
        if job:
            _kernel32()[0].CloseHandle(job)
    return process.returncode, out, err


def _child(argv, cwd):
    # A hung gh or git would stall the wake the dispatcher is waiting on; an executor never runs through here.
    try:
        code, out, err = run_bounded(argv, cwd, CHILD_TIMEOUT)
    except subprocess.TimeoutExpired:
        raise Refusal(f"{' '.join(Path(a).name if i == 0 else a for i, a in enumerate(argv))} "
                      f"took over {CHILD_TIMEOUT} s") from None
    except OSError as error:
        raise Refusal(f"could not start {Path(argv[0]).name}: {error}") from None
    # Never text=True: on Windows that is cp1252, which drops an em dash or Cyrillic from the output.
    return code, out.decode("utf-8", errors="replace"), err.decode("utf-8", errors="replace")


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


def all_issues(root):
    """Every issue, open and closed, with its labels: what each task-list event re-derives a row from."""
    try:
        return issues_from(json.loads(helper(root, "gh-issue-list.sh")))
    except ValueError:
        raise Refusal("gh-issue-list.sh did not print JSON") from None


def pr_merged(root, branch):
    """Whether the branch's pull request into main merged and none is open, read as /ship step 0 reads it."""
    try:
        rows = json.loads(helper(root, "pr-for-branch.sh", branch))
    except ValueError:
        raise Refusal(f"pr-for-branch.sh {branch} did not print JSON") from None
    states = {row.get("state") for row in rows if isinstance(row, dict)} if isinstance(rows, list) else set()
    return "MERGED" in states and "OPEN" not in states


def owned_issues(root):
    """The issues the repository owner opened, which a failed read leaves refused rather than empty or assumed."""
    try:
        data = json.loads(helper(root, "gh-issue-owned.sh"))
    except ValueError:
        raise Refusal("gh-issue-owned.sh did not print JSON") from None
    if not isinstance(data, list) or not all(_number(number) for number in data):
        raise Refusal("gh-issue-owned.sh did not print a list of issue numbers")
    return frozenset(data)


def main_red(root):
    """Each open `main is red` line, none for a green `main`; a read that cannot say green is refused, never read so."""
    code, out, err = _child([git_bash(), ".claude/scripts/main-red-open.sh"], root)
    lines = out.splitlines()
    if (code == 0 and not lines) or (code == 3 and lines):
        return lines
    raise Refusal(f"main-red-open.sh exited {code}: {one_line(err or out, 200)}")


# Command line

def _cell_options(command):
    for name in CALLER_CELLS:
        command.add_argument(f"--{name}")


def _run_less_place(args):
    """`tasks place <n>` with no run is a filing; with a run, `<wave> <group> <n>` as /dispatch step 2 gives it."""
    if args.run is None:
        if len(args.spec) != 1:
            raise Refusal("tasks place without --run takes one issue number; --wave and --group place it")
        args.issue, args.wave, args.group = args.spec[0], args.wave_cell, args.group_cell
        return
    if len(args.spec) != 3 or not re.fullmatch(r"\d+", args.spec[0]) or not re.fullmatch(r"\d+", args.spec[2]):
        raise Refusal("tasks place --run takes <wave> <group> <issue>")
    given = [name for name in CALLER_CELLS if getattr(args, name if name not in ("wave", "group") else
                                                          f"{name}_cell", None) is not None]
    if given:
        raise Refusal(f"tasks place --run reads its cells from place-<n>.json, not --{', --'.join(given)}")
    args.wave, args.group, args.issue = int(args.spec[0]), args.spec[1], int(args.spec[2])


def main(argv=None, body_source=None, open_source=None, branch_source=None, weekly_source=None, owned_source=None,
         red_source=None, issues_source=None, merged_source=None):
    import dispatch_run
    parser = argparse.ArgumentParser(prog="dispatch_board.py")
    sub = parser.add_subparsers(dest="command", required=True)
    b = sub.add_parser("board")
    b.add_argument("--tasks", required=True)
    b.add_argument("--today")
    b.add_argument("--run")
    b.add_argument("--berserk", action="store_true")
    t = sub.add_parser("tasks")
    actions = t.add_subparsers(dest="action", required=True)
    # A day session's `done`, `back`, `place` and `rewrite` name no run; its list is the one the config names.
    day = {"done", "back", "place", "rewrite"}
    for action in ("start", "state", "sync"):
        s = actions.add_parser(action)
        s.add_argument("issue")
        if action == "start":
            s.add_argument("--branch", required=True)
            s.add_argument("--worktree", required=True)
            s.add_argument("--what")
        elif action == "state":
            s.add_argument("text")
            s.add_argument("--branch")
        else:
            _cell_options(s)
    for action in ("init", "reconcile"):
        actions.add_parser(action)
    for action in ("move", "done", "back", "place", "rewrite"):
        s = actions.add_parser(action)
        s.add_argument("--tasks", required=action not in day)
        s.add_argument("--run", required=action not in day)
        s.add_argument("--today")
        if action == "rewrite":
            s.add_argument("--from", dest="source", required=True)
        elif action == "place":
            s.add_argument("spec", nargs="+")
            for name in CALLER_CELLS:
                s.add_argument(f"--{name}", dest=f"{name}_cell" if name in ("wave", "group") else name)
        else:
            s.add_argument("group")
        if action in ("move", "done"):
            s.add_argument("--branch")
        if action == "move":
            s.add_argument("--worktree")
    dispatch_run.add_commands(sub)
    # The task actions resolve their run under the root too, so each takes it after its own name.
    for command in [c for name, c in sub.choices.items() if name != "tasks"] + list(actions.choices.values()):
        command.add_argument("--root", default=".")
    args = parser.parse_args(argv)
    if args.command == "tasks" and args.action in ("done", "back") and args.run is None:
        args.issue, args.group = args.group, None
    root = args.root
    bodies_from = body_source or (lambda number: issue_body(root, number))
    open_from = open_source or (lambda: open_issues(root))
    branch_from = branch_source or (lambda branch: branch_paths(root, branch))
    owned_from = owned_source or (lambda: owned_issues(root))
    red_from = red_source or (lambda: main_red(root))
    issues_from_ = issues_source or (lambda: all_issues(root))
    merged_from = merged_source or (lambda branch: pr_merged(root, branch))
    try:
        if args.command == "tasks" and args.action == "place":
            _run_less_place(args)
        # A group names files under the run, so only a group letter and number, or a sync's, may reach a path.
        known = ROW_GROUP if args.command == "tasks" else GROUP_ID
        if getattr(args, "group", None) is not None and not re.fullmatch(known.pattern, args.group):
            raise Refusal(f"{args.group!r} is not a group id")
        try:
            today = date.fromisoformat(args.today) if getattr(args, "today", None) else date.today()
        except ValueError:
            raise Refusal(f"--today {args.today!r} is not an ISO date") from None
        if args.command == "board":
            # Only a run `start` opened takes the copy and the bodies, so a mistyped --run creates nothing.
            directory = dispatch_run.run_dir(root, args.run) if args.run else None
            flag = args.berserk, frozenset()
            if directory is not None:
                if args.berserk:
                    raise Refusal("--berserk is a dry run's: a run holds its own in state.json")
                flag = dispatch_run.berserk(dispatch_run.load_state(directory))
            text = _read(args.tasks)
            if directory is not None:
                # What this board read is the only base `tasks rewrite` accepts a rebuild from, so it is kept
                # before anything below can refuse: a list the board refuses is the one a rebuild must repair.
                _write(directory / READ_COPY, text)
            bodies = _bodies(parse_tasks(text), directory, bodies_from)
            print("\n".join(board(text, today, bodies, set(open_from()), branch_from, *flag, owned=owned_from())))
        elif args.command == "tasks" and getattr(args, "run", None) is None:
            _day_command(args, issues_from_, merged_from, owned_from)
        elif args.command == "tasks":
            if not args.tasks:
                raise Refusal(f"tasks {args.action} --run takes --tasks")
            _tasks_command(args, today)
        else:
            return dispatch_run.command(args, today, bodies_from, open_from, branch_from, weekly_source, owned_from,
                                        red_from)
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
