"""The owner's answers to /dispatch's interview, written into an issue and acted on.

`record` appends one dated question-and-answer line under `## Owner decisions` in the issue body; `unblock` removes
the `question` or `paused` label; `close` comments and closes as not planned. Exit 2 is a refusal, nothing was
written; exit 3 is a failed `gh` call.
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from datetime import date
from pathlib import Path

HEADING = "## Owner decisions"
PART_LIMIT = 500
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


def read_utf8(path):
    try:
        return Path(path).read_bytes().decode("utf-8")
    except (OSError, UnicodeDecodeError) as error:
        raise Refusal(f"cannot read {path} as UTF-8: {error}")


def gh(*args):
    exe = shutil.which("gh")
    if exe is None:
        raise GhFailure("gh is not on PATH")
    done = subprocess.run([exe, *args], capture_output=True)
    if done.returncode != 0:
        detail = done.stderr.decode("utf-8", errors="replace").strip()
        raise GhFailure(f"gh {' '.join(args[:2])} exited {done.returncode}: {detail}")
    return done.stdout.decode("utf-8", errors="replace")


def repository():
    name = gh("repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner").strip()
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", name):
        raise GhFailure(f"gh repo view answered {name!r}")
    return name


def record(number, question, answer, day, run_dir):
    line = entry(day, question, answer)
    repo = repository()
    view = gh("issue", "view", number, "--repo", repo, "--json", "body")
    try:
        body = json.loads(view)["body"]
    except (ValueError, KeyError, TypeError) as error:
        raise GhFailure(f"gh issue view answered no body: {error}")
    new_body = append_decision(body or "", line)
    directory = tempfile.mkdtemp(dir=run_dir)
    try:
        path = Path(directory) / "body.md"
        path.write_bytes(new_body.encode("utf-8"))
        gh("issue", "edit", number, "--repo", repo, "--body-file", str(path))
    finally:
        shutil.rmtree(directory, ignore_errors=True)


def main(argv=None):
    parser = argparse.ArgumentParser(prog="issue_decisions.py")
    sub = parser.add_subparsers(dest="command", required=True)
    rec = sub.add_parser("record")
    rec.add_argument("number")
    rec.add_argument("--question-file", required=True)
    rec.add_argument("--answer-file", required=True)
    rec.add_argument("--date")
    rec.add_argument("--run-dir")
    unb = sub.add_parser("unblock")
    unb.add_argument("number")
    unb.add_argument("label")
    clo = sub.add_parser("close")
    clo.add_argument("number")
    clo.add_argument("--comment-file", required=True)
    try:
        args = parser.parse_args(argv)
    except SystemExit as stop:
        return 2 if stop.code else 0
    try:
        number = issue_number(args.number)
        if args.command == "record":
            try:
                day = date.fromisoformat(args.date) if args.date else date.today()
            except ValueError:
                raise Refusal(f"--date {args.date!r} is not an ISO date")
            run_dir = args.run_dir if args.run_dir and os.path.isdir(args.run_dir) else None
            record(number, read_utf8(args.question_file), read_utf8(args.answer_file), day, run_dir)
        elif args.command == "unblock":
            label = unblock_label(args.label)
            gh("issue", "edit", number, "--repo", repository(), "--remove-label", label)
        else:
            read_utf8(args.comment_file)
            repo = repository()
            gh("issue", "comment", number, "--repo", repo, "--body-file", args.comment_file)
            gh("issue", "close", number, "--repo", repo, "--reason", "not planned")
    except Refusal as refusal:
        print(f"refused: {refusal}", file=sys.stderr)
        return 2
    except GhFailure as failure:
        print(f"gh failed: {failure}", file=sys.stderr)
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
