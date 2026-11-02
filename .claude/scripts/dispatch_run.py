"""The night's run of /dispatch under artifacts/dispatch/<run>/: start, launch, run, result, verify, wait, wake, resume.

Spec sections 6.2 to 6.4, 8 and 9. dispatch_board.py's command line reaches every subcommand here, so one grant
covers both halves; the task list's half is imported from it. Only the dispatcher writes state.json.
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import zoneinfo
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

from dispatch_board import (BRANCH_NAME, MODEL, Group, Refusal, _blocker, _bodies, _held, _number, _read, _row, _write,
                            git, group_claim, groups, helper, one_line, parse_tasks, path_which, pick, ready,
                            row_branch)

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
# A `run` that started no executor: no slot is held, and the group goes back rather than being launched again.
PRE_LAUNCH = "crashed-before-launch"
# Each ends in `tasks back`, its worktree left for the owner, and the report's Needs a human.
HANDED = ("handed-back", "stopped", "crash-hand-back", PRE_LAUNCH)
WINDOW = timedelta(hours=5, minutes=10)
# A limit is read from fields the CLI writes, never from text the executor writes: a limit with a reset is a window
# or a week, which the crash cap never counts, so a forged one would relaunch a session all night.
RATE_LIMIT_KIND = re.compile(r"rate_limit(?:_error|_exceeded)?", re.IGNORECASE)
ISO_STAMP = r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?"
# A time counts as the reset only right after a reset phrase; any other date in the text is about something else.
RESET_PATTERN = re.compile(
    rf"\breset(?:s)?\s+(?:at\s+|on\s+)?(?:(?P<iso>{ISO_STAMP})|(?P<hour>\d{{1,2}})(?![\d-])(?::(?P<minute>\d{{2}}))?"
    r"\s*(?P<half>[ap]\.?m\b\.?)?(?:\s*\((?P<zone>[A-Za-z_]+(?:/[A-Za-z_+-]+)+)\))?)", re.IGNORECASE)
RESET_KEYS = ("resetsAt", "resets_at", "resetAt", "reset_at")
STILL_ACTIVE = 259
ERROR_ACCESS_DENIED = 5
# The executor's command, resolved from PATH when None; a suite points it at a fake, never through the environment.
CLAUDE_COMMAND = None
PROJECTS = Path.home() / ".claude" / "projects"
SLEEP = time.sleep


def _record_move(directory, gid, moved):
    """Record a move before the list is written, and return how to undo the record if that write is refused."""
    path = Path(directory) / "state.json"
    state = load_state(directory)
    before = _read(path)
    # A new move starts the group afresh: what an earlier launch recorded no longer describes it.
    state.setdefault("groups", {})[gid] = moved
    save_state(directory, state)

    def undo():
        # A failure is the caller's to report beside the list's, never swallowed here.
        _write(path, before)
    return undo


def _close(directory, gid, status):
    state = load_state(directory)
    entry = state.get("groups", {}).get(gid)
    if entry is not None:
        # The report names how a group ended, which `done` and `back` would otherwise overwrite.
        entry.update(closed_from=entry.get("status"), status=status)
        save_state(directory, state)


def _note_placed(directory, issue):
    state = load_state(directory)
    placed = state.setdefault("placed", [])
    if issue not in placed:
        placed.append(issue)
        save_state(directory, state)


def now():
    return datetime.now().astimezone()


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
    # A bool is an int to Python, so True would otherwise start a run of one pair.
    if isinstance(pairs, bool) or not isinstance(pairs, int):
        raise Refusal(f"--pairs {pairs!r} is not a whole number")
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


def open_run(root, tasks_path):
    """The newest run `start` resumes, and why it was kept open when that could not be read, or (None, None)."""
    base = Path(root) / "artifacts" / "dispatch"
    if not base.is_dir():
        return None, None
    runs = sorted((p for p in base.iterdir() if p.is_dir() and RUN_NAME.fullmatch(p.name)
                   and (p / "state.json").is_file()), key=lambda p: p.name, reverse=True)
    read = []

    def listed():
        # Read once, and only when a reported run has an unclosed group to judge against it.
        if not read:
            read.append(_in_progress_keys(tasks_path))
        return read[0]
    for path in runs:
        if not (path / "report.md").exists():
            return path.name, None
        held, why = _holds_rows(path, listed)
        if held:
            return path.name, why
    return None, None


# A group whose rows left In progress through this run's `done` or `back`; any other may still hold them.
CLOSED = ("done", "returned")


def _in_progress_keys(tasks_path):
    try:
        return {row.key for row in parse_tasks(_read(tasks_path)).in_progress}, None
    except (OSError, ValueError, Refusal) as error:
        return None, f"the task list {tasks_path} could not be read: {error}"


def _holds_rows(directory, listed):
    """Whether In progress, as `listed()` returns its keys, holds rows of a group this run has not closed.

    Only the run's own `done` and `back` touch those rows, so such a run stays open past its report; a list or a
    state that cannot be read keeps it open too, with why, since closing it could leave its rows to no run.
    """
    try:
        entries = load_state(directory).get("groups", {})
    except Refusal as error:
        return True, str(error)
    for gid, entry in entries.items():
        if isinstance(entry, dict) and entry.get("status") in CLOSED:
            continue
        keys, unread = listed()
        if keys is None:
            return True, unread
        try:
            if set(_field(entry if isinstance(entry, dict) else {}, gid, "keys", _texts)) & keys:
                return True, None
        except Refusal as error:
            return True, str(error)
    return False, None


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
    # A group sent back after a refused run and launched again must not read that old refusal as this launch's.
    try:
        (directory / f"{gid}.refused.json").unlink(missing_ok=True)
    except OSError as error:
        raise Refusal(f"could not remove {gid}'s earlier refusal: {error}") from None
    entry.update(status="running", model=group.model, issues=[r.number for r in group.rows], branch=branch,
                 worktree=worktree, launched=now().isoformat(), crashes=0)
    save_state(directory, state)


def run_session(root, run, gid, description):
    """The dispatcher's background task for one executor, returning when the session ends.

    It writes only its own <group>.pid and <group>.session.json, or <group>.refused.json when it launches nothing,
    never state.json, which only the dispatcher writes, so parallel executors never race one record.
    """
    directory = run_dir(root, run)
    entry = _entry(load_state(directory), gid)
    if entry.get("status") not in ACTIVE:
        raise Refusal(f"{gid} is {entry.get('status') or 'not launched'}, not waiting for an executor")
    marker = directory / f"{gid}.refused.json"
    try:
        marker.unlink(missing_ok=True)
    except OSError as error:
        raise Refusal(f"could not remove the stale {marker}: {error}") from None
    try:
        child, text, started = _launch(root, directory, run, gid, entry, description)
    except Refusal as refusal:
        # The group holds a slot until `result` reads why nothing launched; state.json stays the dispatcher's.
        try:
            _write(marker, json.dumps({"refused": str(refusal), "at": now().isoformat()}, ensure_ascii=False) + "\n")
        except Refusal as unwritten:
            raise Refusal(f"{refusal}; {unwritten}") from None
        raise
    out, err = child.communicate(text.encode("utf-8"))
    ended = now()
    record = {"exit": child.returncode, "started": started.isoformat(), "ended": ended.isoformat(),
              "seconds": round((ended - started).total_seconds()),
              "stdout": out.decode("utf-8", errors="replace"), "stderr": err.decode("utf-8", errors="replace")}
    _write(directory / f"{gid}.session.json", json.dumps(record, ensure_ascii=False, indent=1) + "\n")


def _launch(root, directory, run, gid, entry, description):
    """Everything up to a started executor, which is returned with its prompt and start time; a refusal starts none."""
    # A relaunch's earlier session was read, so it goes before any check can refuse: a refusal left beside its
    # record would make `result` refuse the pair, and the group would hold its slot all night.
    _remove_stale(directory / f"{gid}.json", directory / f"{gid}.session.json")
    # The owner's stop file ends relaunches too, not only the next wake's launches.
    if (directory / "STOP").exists():
        raise Refusal("stop file: the owner ended the night, so nothing more launches")
    text = prompt(_entry_group(gid, entry), description, run)
    flag = model_flag(entry.get("model"))
    worktree = Path(root) / _field(entry, gid, "worktree", _path_text)
    if not worktree.is_dir():
        raise Refusal(f"{entry['worktree']} is not a directory")
    command = list(CLAUDE_COMMAND) if CLAUDE_COMMAND else [path_which("claude")]
    if not command[0]:
        raise Refusal("no claude on PATH")
    _remove_stale(worktree / "artifacts" / "dispatch" / run / f"{gid}.json")
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
    return child, text, started


def _remove_stale(*paths):
    for stale in paths:
        try:
            stale.unlink(missing_ok=True)
        except OSError as error:
            raise Refusal(f"could not remove the stale {stale}: {error}") from None


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
    # A merge with no review end is a relaunch step 0 ended on a PR already merged, so no round ran; whether the
    # group was relaunched is the run's record, not this file's, so _unreviewed judges that.
    unreviewed = outcome != "merged" or data["rounds"] == []
    if review_end not in REVIEW_ENDS and not (review_end is None and unreviewed):
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


# The envelope's fields the executor writes: its closing text, and each refused call's input.
STEERED = ("result", "permission_denials")


def _fields(value, depth=0):
    """(key, value) for every field of an envelope at any depth, but those the executor writes."""
    if depth > 20:
        return
    if isinstance(value, dict):
        for key, item in value.items():
            if depth == 0 and key in STEERED:
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
    limited, resets = False, None
    if is_error:
        fields = list(_fields(envelope)) if envelope else []
        refused = ((envelope or {}).get("api_error_status") == 429
                   or any(isinstance(v, str) and RATE_LIMIT_KIND.fullmatch(v) for _, v in fields))
        limited = refused or re.search(rf"\b{RATE_LIMIT_KIND.pattern}\b", stderr, re.IGNORECASE) is not None
        if limited:
            resets = next((s for s in (_parse_stamp(v) for k, v in fields if k in RESET_KEYS) if s), None)
            ended = _parse_stamp(raw.get("ended")) or now()
            # A session the API refused ends on that refusal, so its result is the CLI's notice, not a model turn.
            # stdout with no envelope is never read: nothing in it says who wrote it.
            notice = envelope.get("result") if refused and isinstance(envelope.get("result"), str) else ""
            resets = resets or _text_reset(f"{notice}\n{stderr}", ended)
    session = envelope.get("session_id") if envelope else None
    session = session if isinstance(session, str) and SESSION_ID.fullmatch(session) else None
    return SessionEnd(code, bool(is_error), limited, resets, usage, denials, session)


def classify(end, result, crashes, now):
    if isinstance(result, Result):
        return {"merged": "merged-claimed", "handed-back": "handed-back", "stopped": "stopped"}[result.outcome]
    if end.rate_limited and end.resets_at is not None:
        return "window" if end.resets_at - now <= WINDOW else "week"
    # 2026-11-01-dispatch-spike.md, Prompts: a call the auto classifier refused is a hand-back; a relaunch would
    # meet it again.
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


REVIEWER = "branch-reviewer"


def reviewer_spawned(transcript):
    """Whether the session's transcript shows a branch-reviewer spawn that was not refused; False when unreadable.

    A session that re-enters /ship after a compaction reports only the rounds it ran since, so its result can hold
    none though a round ran before it: the transcript is the session's own record of that round.
    """
    spawned, refused = set(), set()
    try:
        with open(transcript, encoding="utf-8") as handle:
            for line in handle:
                try:
                    message = json.loads(line).get("message")
                    parts = message.get("content")
                except (ValueError, AttributeError, RecursionError):
                    continue
                for part in parts if isinstance(parts, list) else []:
                    if not isinstance(part, dict):
                        continue
                    if (part.get("type") == "tool_use" and part.get("name") in ("Agent", "Task")
                            and isinstance(part.get("input"), dict) and part["input"].get("subagent_type") == REVIEWER):
                        spawned.add(part.get("id"))
                    elif part.get("type") == "tool_result" and part.get("is_error") is True:
                        refused.add(part.get("tool_use_id"))
    except (OSError, TypeError, UnicodeDecodeError):
        return False
    return bool(spawned - refused - {None})


def _pid(directory, gid):
    try:
        return int(_read(Path(directory) / f"{gid}.pid").strip())
    except (OSError, ValueError):
        return None


def _refused(directory, gid, entry):
    """Why this launch's last `run` launched nothing, on one line, or None when it launched or has not run.

    The marker sends the group back unread, so it counts only when no session record exists and it is no older
    than the launch; one beside a session record is refused rather than either being picked.
    """
    path = Path(directory) / f"{gid}.refused.json"
    try:
        data = json.loads(_read(path))
    except FileNotFoundError:
        return None
    except (OSError, ValueError):
        data = None
    if (Path(directory) / f"{gid}.session.json").exists():
        raise Refusal(f"{gid} has both {path.name} and a session record, so which ended its launch is unknown")
    data = data if isinstance(data, dict) else {}
    at, launched = _parse_stamp(data.get("at")), _parse_stamp(entry.get("launched"))
    if at is None or launched is None or at < launched:
        # Not shown to be this launch's: an earlier launch's, or undated.
        return None
    why = data.get("refused")
    return one_line(why, 200) if isinstance(why, str) and why.strip() else f"{path.name} names no reason"


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


UNREVIEWED = "merged with no review round"
# A relaunch: a status only a read session leaves, while the group still waits on an executor.
RELAUNCHED = ("relaunch", "window", "week")


def _unreviewed(entry, result, crashes):
    """Whether a merge claims no round from a session that cannot have reviewed it in an earlier one (ruling R15).

    No rounds is a clean merge on a relaunch whose earlier session was read; from a first session it is one nobody
    reviewed, which lands but goes to Needs a human, unless absorb finds a reviewer in that session's transcript."""
    if not (isinstance(result, Result) and result.outcome == "merged" and not result.rounds):
        return False
    earlier = entry.get("absorbed") is not None
    return not (earlier and (crashes >= 1 or entry.get("status") in RELAUNCHED))


def absorb(root, run, gid):
    """Copy, validate and classify one executor's result and session, record them, and say what happened."""
    directory = run_dir(root, run)
    state = load_state(directory)
    entry = _entry(state, gid)
    if entry.get("status") not in ACTIVE:
        raise Refusal(f"{gid} is {entry.get('status') or 'not launched'}, not waiting on a result")
    refused = _refused(directory, gid, entry)
    if refused is not None:
        # Nothing ran, so there is no result to read: the slot is freed and the group goes back, never relaunched.
        reason = f"run refused before launch: {refused}"
        entry.update(status=PRE_LAUNCH, reason=reason)
        save_state(directory, state)
        return [f"{gid}: {PRE_LAUNCH} - {reason}", "transcript: not found"]
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
    transcript = transcript_path(str(Path(root) / worktree), end.session_id, _launch_time(directory, gid, entry))
    # Only a transcript the envelope names is this session's: the newest-file fallback may be another's.
    named = transcript if end.session_id is not None else None
    flagged = _unreviewed(entry, result, crashes)
    seen = flagged and reviewer_spawned(named)
    unreviewed = flagged and not seen
    if unreviewed:
        reason = UNREVIEWED
    # Summed over every session the group ran this move, so a relaunched group is not reported as its last session.
    spent = entry.get("usage") if isinstance(entry.get("usage"), dict) else {}
    usage = {key: (spent[key] if _count(spent.get(key)) else 0) + end.usage.get(key, 0) for key in TOKEN_KEYS
             if key in spent or key in end.usage}
    entry.update(status=decision, absorbed=raw.get("started"), exit=end.exit_code, session_id=end.session_id,
                 usage=usage, denials=end.denials, reason=reason, unreviewed=unreviewed,
                 review_seen="transcript" if seen else None)
    if decision in ("relaunch", "crash-hand-back"):
        entry["crashes"] = crashes + 1
    if end.resets_at is not None:
        entry["resets_at"] = end.resets_at.isoformat()
    if isinstance(result, Result):
        entry.update(pr=result.pr, merge_oid=result.merge_oid, filed=list(result.filed),
                     review_end=result.review_end, rounds=list(result.rounds), decisions=list(result.decisions))
    entry["transcript"] = transcript
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
    # In steps, so the owner's stop file ends a five-hour wait within a minute; a reset already past still reads it.
    while True:
        if (directory / "STOP").exists():
            return "stop file: yes"
        if left <= 0:
            break
        step = min(60.0, left)
        SLEEP(step)
        left -= step
    return f"waited {total:.0f} s"


# The seven_day window exists only to mods inside a running session (2026-11-01-dispatch-spike.md, Weekly figure),
# so no script reads it. A source, when one is found, returns {"percentUsed": <0 to 100>, "resetsAt": <ISO stamp>}.
WEEKLY_SOURCE = None
WEEKLY_STOP_PERCENT_LEFT = 1


@dataclass(frozen=True)
class WeeklyReading:
    percent_left: float
    resets_at: datetime


class ReadingFailed(Exception):
    pass


def read_weekly(source=WEEKLY_SOURCE):
    """The week's figure, or None when there is no source to read it from; a source that fails raises."""
    if source is None:
        return None
    try:
        raw = source()
        used, stamp = raw["percentUsed"], raw["resetsAt"]
        if isinstance(used, bool) or not isinstance(used, (int, float)) or not 0 <= used <= 100:
            raise ValueError(f"percentUsed {used!r} is not a number from 0 to 100")
        resets = datetime.fromisoformat(stamp)
        if resets.tzinfo is None:
            raise ValueError(f"resetsAt {stamp!r} has no zone")
    except Exception as error:
        raise ReadingFailed(str(error) or type(error).__name__) from None
    return WeeklyReading(100.0 - used, resets)


def week_ends_night(reading):
    # Only a reading ends the night on the week; an unread or failed one never does.
    return reading is not None and reading.percent_left <= WEEKLY_STOP_PERCENT_LEFT


def usage_line(reading, failure):
    if failure is not None:
        return f"weekly: reading failed: {failure}"
    if reading is None:
        # read_weekly returns None only without a source, which the spike found none of (WEEKLY_SOURCE's comment).
        return "weekly: not read, no source is set"
    return f"weekly: {reading.percent_left:g}% left, resets {reading.resets_at.isoformat()}"


def take_reading(directory, source):
    """Read the week, record it in state.json, and return the reading and the failure, one of them None."""
    reading = failure = None
    try:
        reading = read_weekly(source)
    except ReadingFailed as error:
        failure = str(error)
    state = load_state(directory)
    usage = state.setdefault("usage", {})
    stamp = now().isoformat()
    if failure is not None:
        # A count and the latest reason, so a source broken all night does not grow state.json at every wake.
        earlier = usage.get("failed") if isinstance(usage.get("failed"), dict) else {}
        count = earlier.get("count") if _count(earlier.get("count")) else 0
        usage["failed"] = {"count": count + 1, "at": stamp, "why": failure}
    elif reading is not None:
        # Only a real reading is a point, so a first wake with no source leaves the start for a later reading.
        point = {"at": stamp, "percent_left": reading.percent_left, "resets_at": reading.resets_at.isoformat()}
        usage["end" if "start" in usage else "start"] = point
    save_state(directory, state)
    return reading, failure


def wake(root, run, tasks_path, today, body_source, open_source, branch_source, weekly_source=None):
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
        # A relaunch keeps its read status while its new executor runs, so the pid file's time finds the live one.
        live = transcript_path(str(worktree), None, _launch_time(directory, gid, entry))
        out.append(f"transcript {gid}: {live or 'none yet'}")
    pairs = state.get("pairs")
    if not _number(pairs):
        raise Refusal(f"{directory / 'state.json'} has no whole number of pairs")
    slots = max(0, pairs - len(active))
    out.append(f"free slots: {slots}")
    if stop:
        return out + ["end: stop-file"]
    # Every wake says what the week read, failed or not; the next tries again, and the executor's limit stays below.
    reading, failure = take_reading(directory, weekly_source)
    out.append(usage_line(reading, failure))
    if week_ends_night(reading):
        return out + ["end: week"]
    if any(entry.get("status") == "week" and not _week_passed(entry, current) for entry in entries.values()):
        return out + ["end: week"]
    tasks = parse_tasks(_read(tasks_path))
    bodies = _bodies(tasks, directory, body_source)
    open_set = set(open_source())
    candidates = groups(tasks)
    ready_map = {g.id: ready(g, open_set, today, bodies) for g in candidates}
    claims = {g.id: group_claim(g, bodies) for g in candidates}
    # A moved group's In progress row has lost its conflicts cell, so the claim its Remaining rows made is kept.
    held_keys = {r.key for r in tasks.in_progress}
    running = [_entry_group(gid, entry) for gid, entry in entries.items()
               if "rows" in entry and set(_field(entry, gid, "keys", _texts)) & held_keys]
    claims.update({g.id: group_claim(g, bodies) for g in running})
    taken = [claim for claim, _ in _held(tasks, bodies, branch_source)]
    chosen = pick(candidates + running, ready_map, claims, [g.id for g in running], taken, slots)
    if chosen:
        return out + [f"launch: {' '.join(chosen)}"]
    if active:
        return out + ["launch: none"]
    # With nothing running every slot is free, so a ready group left unchosen is held by an In progress row.
    return out + ["end: held" if any(ok for ok, _ in ready_map.values()) else "end: drained"]


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
            try:
                refused = _refused(directory, gid, entry)
            except Refusal:
                # A marker beside a session record: the session record's line below sends it to `result`, which
                # refuses the pair.
                refused = None
            if refused is not None:
                out.append(f"result {gid}: its run launched nothing and was not read")
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
        elif status in HANDED:
            out.append(f"back {gid}: {status}")
    return out


# The morning report's facts (spec section 10), in English; the dispatcher writes report.md from them in --lang.

REPORT_SECTIONS = ("Merged", "Needs a human", "Interrupted", "Filed overnight", "Left in the backlog", "Usage")
TOKEN_NAMES = ("input", "cache write", "cache read", "output")
UNFINISHED = {None: "moved, never launched", "merged-claimed": "merge claimed, not yet verified"}


def _issues_text(numbers):
    return ", ".join(f"#{n}" for n in numbers if _number(n)) if isinstance(numbers, list) and numbers else "none"


def _rounds_text(rounds):
    if not isinstance(rounds, list) or not rounds:
        return "no review rounds in this session"
    kinds = [f"{r.get('kind')} raised {r.get('raised')} fixed {r.get('fixed')} refused {r.get('refused')}"
             for r in rounds if isinstance(r, dict)]
    return f"rounds {len(rounds)}: " + "; ".join(kinds)


def _decisions_text(decisions):
    # Each is the executor's text: one_line keeps a line break in it from forging a report line of its own.
    if not isinstance(decisions, list) or not decisions:
        return ""
    return "; decisions: " + " / ".join(one_line(d, 200) for d in decisions if isinstance(d, str))


def _weekly_lines(usage):
    usage = usage if isinstance(usage, dict) else {}
    out = []
    for when in ("start", "end"):
        point = usage.get(when)
        if isinstance(point, dict) and point.get("percent_left") is not None:
            out.append(f"weekly at the {when}: {point['percent_left']:g}% left, resets {point.get('resets_at')}")
    # The owner's decision: with no reading the limit is ignored, and the report says it was not read.
    out = out or ["weekly: not read"]
    failed = usage.get("failed")
    if isinstance(failed, dict) and _count(failed.get("count")) and failed["count"]:
        out.append(f"weekly: reading failed, {failed['count']} in all, the last at {failed.get('at')}: "
                   f"{one_line(failed.get('why'), 200)}")
    return out


def report(root, run, tasks_path, today, body_source, open_source):
    directory = run_dir(root, run)
    state = load_state(directory)
    entries = {gid: entry for gid, entry in state.get("groups", {}).items() if isinstance(entry, dict)}
    merged, needs, interrupted, usage = [], [], [], []
    filed = [n for entry in entries.values() for n in (entry.get("filed") or []) if _number(n)]
    filed += [n for n in state.get("placed") or [] if _number(n)]
    filed = list(dict.fromkeys(filed))
    tasks = parse_tasks(_read(tasks_path))
    listed = {row.key for row in tasks.in_progress}
    for gid, entry in entries.items():
        status = entry.get("status")
        ended = entry.get("closed_from") if status in ("done", "returned") else status
        if ended == "verified" and status == "done" and entry.get("unreviewed"):
            # It landed, but nobody reviewed it: the one kind of merge that is not a merge needing nothing.
            needs.append(f"{gid} #{entry.get('pr')} {UNREVIEWED}, as {entry.get('merge_oid')}: "
                         f"{_issues_text(entry.get('issues'))}; filed {_issues_text(entry.get('filed'))}"
                         + _decisions_text(entry.get("decisions")))
        elif ended == "verified" and status == "done":
            # Cleared by its transcript, it reported no round: saying so beats "review not run", which it was not.
            review = ("no rounds reported; a branch-reviewer round in its transcript"
                      if entry.get("review_seen") == "transcript"
                      else f"{_rounds_text(entry.get('rounds'))}; review {entry.get('review_end') or 'not run'}")
            merged.append(f"{gid} #{entry.get('pr')} merged as {entry.get('merge_oid')}: "
                          f"{_issues_text(entry.get('issues'))}; {review}; filed {_issues_text(entry.get('filed'))}"
                          + _decisions_text(entry.get("decisions")))
        elif status == "verified":
            # GitHub showed the merge, but the pull or the containment read after it failed, so `done` never ran.
            unreviewed = f", {UNREVIEWED}" if entry.get("unreviewed") else ""
            needs.append(f"{gid} #{entry.get('pr')} merged as {entry.get('merge_oid')}{unreviewed}, "
                         "not confirmed in main: "
                         f"its rows are still In progress and worktree {entry.get('worktree')} is left in place"
                         + _decisions_text(entry.get("decisions")))
        elif ended in HANDED or status == "returned":
            worktree = entry.get("worktree")
            kept = _path_text(worktree) and (Path(root) / worktree).is_dir()
            needs.append(f"{gid} {ended or 'returned'}: {one_line(entry.get('reason') or 'no reason recorded')}; "
                         f"worktree {worktree} {'left in place' if kept else 'removed'}"
                         + _decisions_text(entry.get("decisions")))
        elif status in ACTIVE and not (_path_text(entry.get("worktree"))
                                       and (Path(root) / entry["worktree"]).is_dir()):
            # `resume` refuses it, so no night relaunches it: while its rows are listed they hold the run open.
            keys = entry.get("keys") if isinstance(entry.get("keys"), list) else []
            rows = (f"its In progress rows keep run {run} open until they are sent back or removed"
                    if listed & set(map(str, keys)) else "its rows are no longer In progress")
            needs.append(f"{gid} {status}: its worktree {entry.get('worktree')} is gone; {rows}"
                         + _decisions_text(entry.get("decisions")))
        else:
            interrupted.append(f"{gid} {UNFINISHED.get(status, status)}")
        spent = entry.get("usage") if isinstance(entry.get("usage"), dict) else {}
        if spent:
            counts = [spent.get(key) if _count(spent.get(key)) else 0 for key in TOKEN_KEYS]
            usage.append(f"{gid}: {sum(counts)} tokens ("
                         + ", ".join(f"{name} {n}" for name, n in zip(TOKEN_NAMES, counts)) + ")")
    if _holds_rows(directory, lambda: (listed, None))[0]:
        # The same test `start` reopens by, so the line appears exactly when the next /dispatch resumes this run.
        interrupted.append(f"resume: run /dispatch again in the main checkout; it resumes run {run}")
    bodies = _bodies(tasks, directory, body_source)
    open_set = set(open_source())
    left = []
    for group in groups(tasks):
        ok, why = ready(group, open_set, today, bodies)
        left.append(f"{group.id} wave {group.wave}: {'ready, not launched before the night ended' if ok else why}")
        needs += [f"#{row.number}: filed overnight, waits on a human" for row in group.rows
                  if row.number in filed and _blocker(row, open_set, today, bodies) == "waits on a human"]
    left.append(f"Tracking: {len(tasks.tracking)} rows and Paused: {len(tasks.paused)} rows, which no night runs")
    spent = (usage or ["executors: no usage read"]) + _weekly_lines(state.get("usage"))
    out = []
    for name, lines in zip(REPORT_SECTIONS, (merged, needs, interrupted, [_issues_text(filed)], left, spent)):
        out += [f"## {name}", *(lines or ["none"])]
    return out


# Command line, reached through dispatch_board.py's main

def add_commands(sub):
    s = sub.add_parser("start")
    s.add_argument("--pairs", type=int, required=True)
    s.add_argument("--lang", default="en")
    s.add_argument("--tasks", required=True)
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
    sub.add_parser("usage").add_argument("run")
    r = sub.add_parser("report")
    r.add_argument("run")
    r.add_argument("--tasks", required=True)
    r.add_argument("--today")


def command(args: argparse.Namespace, today, bodies_from, open_from, branch_from, weekly_from=None):
    """Run one of add_commands' subcommands and return its exit code; a Refusal is the caller's to print."""
    root = args.root
    if args.command == "start":
        _start_values(args.pairs, args.lang)
        existing, why = open_run(root, args.tasks)
        if existing:
            print(existing)
            if why:
                print(f"kept open: {why}", file=sys.stderr)
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
        print("\n".join(wake(root, args.run, args.tasks, today, bodies_from, open_from, branch_from, weekly_from)))
    elif args.command == "usage":
        print(usage_line(*take_reading(run_dir(root, args.run), weekly_from)))
    elif args.command == "report":
        print("\n".join(report(root, args.run, args.tasks, today, bodies_from, open_from)))
    else:
        print("\n".join(resume(root, args.run)))
    return 0
