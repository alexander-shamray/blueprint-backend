#!/usr/bin/env python3
"""Every commit a pull request carries names that pull request and keeps /commit's form.
The rules are .github/commit-gate/README.md's; run it as
py -3.12 .github/commit-gate/commit_gate.py --pr <n> <base> <head>.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys

# The types /commit lists. `feat` alone carries a mandatory scope, as the
# command's own form says; the rest take one optionally.
SUBJECT = re.compile(
    r"^(?:feat\([^()\s][^()]*\)|(?:docs|fix|chore|refactor|test)(?:\([^()\s][^()]*\))?): \S"
)

# A revert git wrote itself: `git revert --no-edit` titles it `Revert "<the
# reverted subject>"`, so the form is judged on the subject inside the quotes.
REVERT = re.compile(r'^Revert "(?P<inner>.+)"$')

LEFTOVER = re.compile(r"^(?:fixup!|squash!|amend!|wip(?![a-z]))", re.IGNORECASE)

TRAILER_KEY = "PR"


def subject_problem(subject: str) -> str | None:
    """Why the subject is outside the form, or None when it is inside it."""
    revert = REVERT.match(subject)
    inner = revert.group("inner") if revert else subject
    if LEFTOVER.match(subject) or LEFTOVER.match(inner):
        return "is a fixup!/squash!/amend!/wip leftover"
    if not SUBJECT.match(inner):
        return "is outside /commit's form (<type>(<scope>): <what changed>)"
    return None


def check(pr: int, commits: list[dict]) -> list[str]:
    """Every problem in the range; an empty list is a pass."""
    if not commits:
        return ["the range holds no commits, so there is nothing to have judged"]
    problems: list[str] = []
    for commit in commits:
        sha = commit["sha"][:12]
        subject = commit["subject"]
        if len(commit["parents"]) > 1:
            problems.append(f"{sha} is a merge commit; land the branch by rebase")
        reason = subject_problem(subject)
        if reason:
            problems.append(f'{sha} "{subject}" {reason}')
        named = [value for key, value in commit["trailers"] if key == TRAILER_KEY]
        wanted = f"#{pr}"
        if not named:
            problems.append(f"{sha} carries no `{TRAILER_KEY}: {wanted}` trailer")
        elif any(value != wanted for value in named):
            problems.append(
                f"{sha} carries `{TRAILER_KEY}: {', '.join(named)}`, not `{TRAILER_KEY}: {wanted}`"
            )
    return problems


def _git(*args: str, stdin: str | None = None) -> str:
    done = subprocess.run(
        ["git", *args], input=stdin, capture_output=True, text=True, encoding="utf-8", check=False
    )
    if done.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {done.stderr.strip()}")
    return done.stdout


def _trailers(message: str) -> list[tuple[str, str]]:
    pairs = []
    for line in _git("interpret-trailers", "--parse", stdin=message).splitlines():
        key, separator, value = line.partition(":")
        if separator:
            pairs.append((key.strip(), value.strip()))
    return pairs


def collect(base: str, head: str) -> list[dict]:
    """The commits of base..head, oldest first, as `check` reads them."""
    commits = []
    for sha in _git("rev-list", "--reverse", f"{base}..{head}").split():
        message = _git("show", "-s", "--format=%B", sha)
        commits.append(
            {
                "sha": sha,
                "parents": _git("show", "-s", "--format=%P", sha).split(),
                "subject": message.split("\n", 1)[0],
                "trailers": _trailers(message),
            }
        )
    return commits


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--pr", type=int, required=True)
    parser.add_argument("base")
    parser.add_argument("head")
    args = parser.parse_args(argv)
    try:
        problems = check(args.pr, collect(args.base, args.head))
    except RuntimeError as error:
        print(f"commit-gate: {error}", file=sys.stderr)
        return 2
    for problem in problems:
        print(f"commit-gate: {problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
