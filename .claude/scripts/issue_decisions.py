"""The owner's answers to /dispatch's interview, written into an issue and acted on.

`record` appends a dated answer under `## Owner decisions`, `unblock` drops `question` or `paused`, `close` comments
and closes as not planned, `comment` only comments; exit 2 is a refusal with nothing written, 3 a failed `gh` call.
The untrusted body is searched for the heading line only, so one in a fence counts; a retried `close` comments twice."""

import argparse
import json
import os
import re
import subprocess
import sys
from datetime import date
from pathlib import Path

# The checkout this file is in, never a caller's path: a run is resolved under it as dispatch_run's run_dir does.
ROOT = Path(__file__).resolve().parents[2]
RUN_NAME = re.compile(r"\d{8}-\d{6}")
HEADING = "## Owner decisions"
PART_LIMIT = 500
# A close comment says why no work is needed: a few answers' worth, never a pasted file.
COMMENT_LIMIT = 4 * PART_LIMIT
LABELS = ("question", "paused")
ENTRY_RE = re.compile(
    r"- \d{4}-\d{2}-\d{2}: Q: (?P<question>[^\r\n#][^\r\n]{0,%d}) A: (?P<answer>[^\r\n#][^\r\n]{0,%d})"
    % (PART_LIMIT - 1, PART_LIMIT - 1)
)
ANY_HEADING = re.compile(r"#{1,6}\s")


class Refusal(Exception):
    pass


class GhFailure(Exception):
    pass


def part(name, text):
    text = text.strip()
    if not text:
        raise Refusal(f"the {name} is empty")
    if "\n" in text or "\r" in text:
        raise Refusal(f"the {name} is more than one line")
    if text.startswith("#"):
        raise Refusal(f"the {name} opens with #, which would read as a heading")
    if len(text) > PART_LIMIT:
        raise Refusal(f"the {name} is over {PART_LIMIT} characters")
    # Read as the entry holds it, between "Q: " and " A: ", so a question opening or closing on the marker counts.
    if name == "question" and " A: " in f" {text} ":
        raise Refusal("the question holds 'A: ', which a later split would read as the answer")
    return text


def entry(day, question, answer):
    line = f"- {day.isoformat()}: Q: {part('question', question)} A: {part('answer', answer)}"
    if not ENTRY_RE.fullmatch(line):
        raise Refusal("the entry does not have the shape - <date>: Q: <question> A: <answer>")
    return line


def append_decision(body, line):
    # The body is the issue author's text: only the heading line is located, nothing else in it is read.
    crlf = "\r\n" in body
    eol = "\r" if crlf else ""
    lines = body.split("\n")
    at = [i for i, text in enumerate(lines) if text.rstrip("\r") == HEADING]
    if len(at) > 1:
        raise Refusal(f"the issue body holds {HEADING!r} {len(at)} times")
    if not body.strip():
        # Nothing to separate the section from, so it opens the body rather than following blank lines.
        return f"{HEADING}\n\n{line}\n"
    if not at:
        return body.rstrip("\r\n") + f"{eol}\n{eol}\n{HEADING}{eol}\n{eol}\n{line}{eol}\n"
    start = at[0]
    end = len(lines)
    for i in range(start + 1, len(lines)):
        if ANY_HEADING.match(lines[i]):
            end = i
            break
    last = start
    for i in range(start + 1, end):
        if lines[i].strip():
            last = i
    if last == start:
        lines[start + 1:start + 1] = [eol, line + eol]
    else:
        lines.insert(last + 1, line + eol)
    return "\n".join(lines)


def unblock_label(label):
    if label not in LABELS:
        raise Refusal(f"label {label!r} is not one of {', '.join(LABELS)}")
    return label


def issue_number(text):
    if not re.fullmatch(r"[1-9][0-9]{0,8}", text):
        raise Refusal(f"issue {text!r} is not a number")
    return text


def run_directory(run):
    """artifacts/dispatch/<run> under ROOT, refused unless the name is a run's and `start` opened it."""
    if not isinstance(run, str) or not RUN_NAME.fullmatch(run):
        raise Refusal(f"run name {run!r} is not a yyyymmdd-hhmmss timestamp")
    directory = ROOT / "artifacts" / "dispatch" / run
    if not (directory / "state.json").is_file():
        raise Refusal(f"{directory / 'state.json'} is missing; the run was never started")
    return directory


def run_file(path, directory, option, limit):
    """A file under the run's directory, read once and at most `limit` characters, as UTF-8 text."""
    real, base = os.path.normcase(os.path.realpath(path)), os.path.normcase(os.path.realpath(directory))
    try:
        inside = real != base and os.path.commonpath([real, base]) == base
    except ValueError:
        # Windows' commonpath refuses two drives, which are never one inside the other.
        inside = False
    if not inside:
        raise Refusal(f"{option} {path} is not under the run's directory {directory}")
    try:
        size = os.path.getsize(real)
    except OSError as error:
        raise Refusal(f"cannot read {path}: {error}")
    # Bytes first, so an oversized file is refused before it is read; a character is at most four bytes.
    if size > 4 * limit:
        raise Refusal(f"{option} {path} is over {limit} characters")
    try:
        text = Path(real).read_bytes().decode("utf-8")
    except (OSError, UnicodeDecodeError) as error:
        raise Refusal(f"cannot read {path} as UTF-8: {error}")
    if len(text) > limit:
        raise Refusal(f"{option} {path} is over {limit} characters")
    return text


def comment_text(path, directory):
    """The close comment, refused unless it is a file the run wrote under its own directory and of an answer's size."""
    text = run_file(path, directory, "--comment-file", COMMENT_LIMIT)
    if not text.strip():
        raise Refusal("the comment is empty")
    if len(text) > COMMENT_LIMIT:
        raise Refusal(f"the comment is over {COMMENT_LIMIT} characters")
    return text


def path_which(name):
    """The first executable `name` on PATH, never one in the current directory, or None.

    shutil.which looks in the current directory first on Windows, where a merged change could plant gh.cmd. Copied
    from dispatch_board because this helper imports nothing else of the dispatcher's."""
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


def gh(*args, stdin=None):
    exe = path_which("gh")
    if exe is None:
        raise GhFailure("gh is not on PATH")
    try:
        done = subprocess.run([exe, *args], input=stdin, capture_output=True)
    except OSError as error:
        raise GhFailure(f"gh {' '.join(args[:2])} could not start: {error}")
    if done.returncode != 0:
        detail = done.stderr.decode("utf-8", errors="replace").strip()
        raise GhFailure(f"gh {' '.join(args[:2])} exited {done.returncode}: {detail}")
    return done.stdout.decode("utf-8", errors="replace")


def repository():
    name = gh("repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner").strip()
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", name):
        raise GhFailure(f"gh repo view answered {name!r}")
    return name


def record(number, question, answer, day):
    line = entry(day, question, answer)
    repo = repository()
    view = gh("issue", "view", number, "--repo", repo, "--json", "body")
    try:
        body = json.loads(view)["body"]
    except (ValueError, KeyError, TypeError) as error:
        raise GhFailure(f"gh issue view answered no body: {error}")
    new_body = append_decision(body or "", line)
    gh("issue", "edit", number, "--repo", repo, "--body-file", "-", stdin=new_body.encode("utf-8"))


def main(argv=None):
    parser = argparse.ArgumentParser(prog="issue_decisions.py")
    sub = parser.add_subparsers(dest="command", required=True)
    rec = sub.add_parser("record")
    rec.add_argument("number")
    rec.add_argument("--question-file", required=True)
    rec.add_argument("--answer-file", required=True)
    rec.add_argument("--date")
    rec.add_argument("--run", required=True)
    unb = sub.add_parser("unblock")
    unb.add_argument("number")
    unb.add_argument("label")
    for name in ("close", "comment"):
        clo = sub.add_parser(name)
        clo.add_argument("number")
        clo.add_argument("--comment-file", required=True)
        clo.add_argument("--run", required=True)
    try:
        args = parser.parse_args(argv)
    except SystemExit as stop:
        if stop.code:
            # argparse has printed its usage; the prefix is what the runbook reads exit 2 by.
            print("refused: the command line does not match the usage above", file=sys.stderr)
        return 2 if stop.code else 0
    try:
        number = issue_number(args.number)
        if args.command == "record":
            try:
                day = date.fromisoformat(args.date) if args.date else date.today()
            except ValueError:
                raise Refusal(f"--date {args.date!r} is not an ISO date")
            directory = run_directory(args.run)
            # Both reach a public issue, so each is held to the run, as the close comment is.
            question = run_file(args.question_file, directory, "--question-file", COMMENT_LIMIT)
            answer = run_file(args.answer_file, directory, "--answer-file", COMMENT_LIMIT)
            record(number, question, answer, day)
        elif args.command == "unblock":
            label = unblock_label(args.label)
            gh("issue", "edit", number, "--repo", repository(), "--remove-label", label)
        else:
            # The text checked here is the text posted: gh reopening the path could read a file swapped since.
            text = comment_text(args.comment_file, run_directory(args.run))
            repo = repository()
            try:
                gh("issue", "comment", number, "--repo", repo, "--body-file", "-", stdin=text.encode("utf-8"))
            except GhFailure as failure:
                raise GhFailure(f"comment step failed, the issue is still open: {failure}")
            if args.command == "close":
                try:
                    gh("issue", "close", number, "--repo", repo, "--reason", "not planned")
                except GhFailure as failure:
                    raise GhFailure(f"close step failed after the comment was posted; a retry posts a second: {failure}")
    except Refusal as refusal:
        print(f"refused: {refusal}", file=sys.stderr)
        return 2
    except GhFailure as failure:
        print(f"gh failed: {failure}", file=sys.stderr)
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
