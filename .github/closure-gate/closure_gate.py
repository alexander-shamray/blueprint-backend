#!/usr/bin/env python3
"""What a pull request says it closes must match what merging it will close.

It says it three times, each handled separately and none compared with the
others except by this gate:

1. The `| Closes |` row in the house body form, which GitHub's linker does not
   read.
2. `closingIssuesReferences`, GitHub's parse of the pull request body.
3. A closing keyword in a commit body, honoured on merge whatever the
   description says, and not editable.

A table pipe between `Closes` and the reference leaves GitHub no
keyword-reference pair, so a row alone closes nothing; and `gh pr view --json
closingIssuesReferences` reports the body only, so a commit keyword the
description omits closes an issue from a place no reviewer looks.

What must agree is what the merge does, `closingIssuesReferences` with the
commit keywords, and what the pull request says, in the table and the
description. An issue the description closes and no commit repeats is the
ordinary case, not a disagreement: comparing that direction would make a commit
keyword mandatory, a rule nothing in this repository states.

The commit half is this file's regex, so a regex that matches too little is a
fail-open: a dropped keyword the description also omits agrees with nothing
missing. The parser is therefore literal, matching inside backticks and quoted
prose as GitHub's linker does, and a keyword-shaped token it cannot resolve is
a problem rather than a skip.

`gh pr view --json commits` returns one page and a prefix looks complete, so a
list at or above `GH_PAGE_SIZE` is refused rather than judged.
`closingIssuesReferences` gets no such guard because `gh` preloads it through
every page, and a guard there would refuse a correct pull request.

Stdlib only, on the licence gate's terms. The deciding takes JSON on stdin and
the fetching is one `gh` call in the workflow, `deploy/canary/canary.py`'s
split:

    gh pr view <n> --json number,url,body,commits,closingIssuesReferences,headRefOid |
        py -3.12 .github/closure-gate/closure_gate.py

A cross-repository `Closes owner/other#<n>` is refused by name: every issue this
repository tracks lives in it, so that form is a mistake or an unconsidered
case.
"""

from __future__ import annotations

import argparse
import json
import re
import sys

# GitHub's documented set, all nine spellings. Written out rather than
# generated, because a generated list is one more thing that can quietly
# produce eight.
_KEYWORD = r"(?:close[sd]?|fix(?:es|ed)?|resolve[sd]?)"

# Every keyword followed by whatever token comes next. Classification happens
# afterwards, and that ordering is the point: a keyword whose reference this
# gate cannot parse is a finding, not a non-match. Matching the reference
# shape here instead would turn every unhandled spelling into silence.
KEYWORD_THEN_TOKEN = re.compile(rf"\b{_KEYWORD}\b\s*:?\s+(?P<token>\S+)", re.IGNORECASE)

# What a token has to look like to be an issue reference. The optional
# `owner/repo` prefix is captured so a cross-repository form can be refused by
# name rather than failing to match and vanishing.
REFERENCE = re.compile(
    r"^(?:(?P<repo>[\w.-]+/[\w.-]+))?#(?P<number>\d+)\b"
    r"|^https?://github\.com/(?P<url_repo>[\w.-]+/[\w.-]+)/issues/(?P<url_number>\d+)\b",
    re.IGNORECASE,
)

# Markup a reference may be wrapped in. A commit body arguing about a keyword
# writes it in backticks, and GitHub links it anyway.
WRAPPERS = "`\"'([{*_<"

# A token this matches is issue-shaped whatever markup wraps it, so an
# unresolved one struck through with `~~` is reported rather than read as
# prose and dropped from the commit set.
ISSUE_SHAPED = re.compile(r"#\d")

# The house form's metadata row: `| Closes | <issue> (high), <issue> (high) |`.
TABLE_ROW = re.compile(
    rf"^[ \t]*\|[ \t]*{_KEYWORD}[ \t]*\|(?P<cell>[^|]*)\|",
    re.IGNORECASE | re.MULTILINE,
)

ISSUE_NUMBER = re.compile(r"#(\d+)\b")

REQUIRED_FIELDS = ("number", "url", "body", "commits", "closingIssuesReferences", "headRefOid")

# One page of `gh pr view`. The commit list is not preloaded, so a list this
# long may be a prefix and a prefix is indistinguishable from the whole thing
# from in here. `closingIssuesReferences` is preloaded through every page and
# is not held to this.
GH_PAGE_SIZE = 100

PULL_URL = re.compile(r"^https?://github\.com/(?P<repo>[\w.-]+/[\w.-]+)/pull/\d+", re.IGNORECASE)


def repository_of(pull_url: str) -> str | None:
    """`owner/name` from the pull request's own URL, so nothing has to be passed in."""
    match = PULL_URL.match(pull_url.strip())
    return match.group("repo") if match else None


def closing_references(text: str, repository: str) -> tuple[set[int], list[str]]:
    """Issue numbers a closing keyword in `text` names, and what could not be read.

    Returns `(numbers, unreadable)`. A keyword followed by ordinary prose —
    "closes the naive spelling and nothing more" — is neither: it is not a
    reference and it is not a failure to read one, so it appears in neither
    half. Only a token that *looks* like a reference and still does not resolve
    to an issue in this repository is reported.
    """
    numbers: set[int] = set()
    unreadable: list[str] = []

    for match in KEYWORD_THEN_TOKEN.finditer(text):
        token = match.group("token").lstrip(WRAPPERS)
        reference = REFERENCE.match(token)
        if reference is None:
            # Prose. `Closes the door` is English, not a link. The substring
            # test below is not a sanitiser: it runs only after REFERENCE has
            # refused the token, and a match widens a rejection rather than
            # granting anything. The trust decision is REFERENCE and the
            # repository comparison below, and both are exact.
            if (token.startswith("#")
                    or ISSUE_SHAPED.search(token)
                    or "github.com/" in token.lower()):
                unreadable.append(match.group(0).strip())
            continue

        named = reference.group("repo") or reference.group("url_repo")
        number = reference.group("number") or reference.group("url_number")
        if named is not None and named.lower() != repository.lower():
            unreadable.append(match.group(0).strip())
            continue
        numbers.add(int(number))

    return numbers, unreadable


def declared_in_table(body: str) -> set[int]:
    """Issue numbers named by the body's `| Closes |` metadata row."""
    return {
        int(number)
        for row in TABLE_ROW.finditer(body)
        for number in ISSUE_NUMBER.findall(row.group("cell"))
    }


def check(payload: dict) -> list[str]:
    problems: list[str] = []

    missing = [field for field in REQUIRED_FIELDS if field not in payload]
    if missing:
        return [
            "the gate was handed JSON with no "
            + ", ".join(f"`{field}`" for field in missing)
            + " — it judged nothing, and a gate that judged nothing must not report a pass"
        ]

    repository = repository_of(payload["url"] or "")
    if repository is None:
        return [f"`url` is not a pull request URL, so the repository is unknown: {payload['url']!r}"]

    commits = payload["commits"]
    if not commits:
        return [
            "this pull request reports no commits at all, which is not a state "
            "the gate can judge — the commit half of the comparison would be "
            "empty for the wrong reason"
        ]

    # A read taken before GitHub has indexed the newest push returns the
    # commit list without it, and a missing commit is a missing keyword. A
    # list not containing `headRefOid` is behind or truncated, so it is
    # refused rather than judged.
    oids = {(commit.get("oid") or "") for commit in commits}
    if payload["headRefOid"] not in oids:
        return [
            f"the commit list does not contain this pull request's head "
            f"({payload['headRefOid'][:8]}), so it is stale or truncated — a "
            f"closing keyword in a commit this gate cannot see is exactly the "
            f"fail-open it exists to refuse. Re-read `gh pr view` and try again"
        ]

    if len(commits) >= GH_PAGE_SIZE:
        return [
            f"this pull request reports {len(commits)} commits, at or above "
            f"the {GH_PAGE_SIZE} that `gh pr view --json commits` returns "
            f"in one page — so the list may be a prefix, and a closing keyword "
            f"past the cut is invisible to this gate for a reason that has "
            f"nothing to do with what the pull request says. That is the "
            f"fail-open shape this gate exists to close, so it refuses rather "
            f"than judging: fetch the commits through a paginated endpoint and "
            f"pass the complete list"
        ]

    linked = {issue["number"] for issue in payload["closingIssuesReferences"]}
    declared = declared_in_table(payload["body"] or "")

    from_commits: dict[int, list[str]] = {}
    for commit in commits:
        oid = (commit.get("oid") or "")[:8] or "(no oid)"
        text = f"{commit.get('messageHeadline', '')}\n{commit.get('messageBody', '')}"
        numbers, unreadable = closing_references(text, repository)
        for number in numbers:
            from_commits.setdefault(number, []).append(oid)
        for phrase in unreadable:
            problems.append(
                f"commit {oid} carries `{phrase}`, which is keyword-shaped and "
                f"names no issue in {repository} — this gate will not guess at it"
            )

    # The body is read only for the keyword-shaped tokens below. What the
    # description *closes* is `closingIssuesReferences`, which is GitHub's own
    # answer rather than this file's, and there is no reason to prefer a second
    # opinion to the authority.
    _, body_unreadable = closing_references(payload["body"] or "", repository)
    for phrase in body_unreadable:
        problems.append(
            f"the description carries `{phrase}`, which is keyword-shaped and "
            f"names no issue in {repository} — this gate will not guess at it"
        )

    for number in sorted(from_commits.keys() - linked):
        oids = ", ".join(sorted(from_commits[number]))
        problems.append(
            f"#{number} is closed by commit {oids} and the description does not "
            f"name it (`closingIssuesReferences` reports "
            f"{sorted(linked) or 'nothing'}). The merge will close it either "
            f"way — a commit message cannot be edited — so reconcile the "
            f"description to the commits, not the commits to the description"
        )

    for number in sorted(declared - linked):
        problems.append(
            f"the `| Closes |` row names #{number} and GitHub linked nothing "
            f"for it. A table pipe between the keyword and the reference means "
            f"there is no keyword-reference pair to read: add a bare "
            f"`Closes #{number}` line below the table"
        )

    for number in sorted(linked - declared):
        problems.append(
            f"merging closes #{number} and the `| Closes |` row does not say "
            f"so, so the summary a reader trusts understates what happens"
        )

    return problems


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "payload",
        nargs="?",
        type=argparse.FileType(encoding="utf-8"),
        default=sys.stdin,
        help="`gh pr view --json ...` output; stdin by default",
    )
    args = parser.parse_args(argv[1:])

    try:
        payload = json.load(args.payload)
    except json.JSONDecodeError as error:
        print(f"closure-gate: the input is not JSON: {error}", file=sys.stderr)
        return 2

    problems = check(payload)
    if problems:
        print(
            f"closure-gate: {len(problems)} problem(s) with what PR "
            f"#{payload.get('number', '?')} says it closes:\n",
            file=sys.stderr,
        )
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    linked = sorted(issue["number"] for issue in payload["closingIssuesReferences"])
    closes = ", ".join(f"#{number}" for number in linked) if linked else "nothing"
    print(
        f"closure-gate: what this pull request says it closes matches what "
        f"merging it will close — {closes}."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
