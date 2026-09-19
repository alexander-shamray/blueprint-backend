#!/usr/bin/env python3
"""A pull request's diff must sit inside the class it declares, and inside
the touch set it declares.

`docs/change-locality.md` asks every PR body to carry two rows, `| Class |`
and `| Touch set |`, and section 3 gives each class a tree set.

Every changed path is judged twice, and a path outside either set fails the
PR and is named in the verdict. The class -> tree-set map in `classes.yml`
beside this file says what a class may reach in this repository, but it cannot
say "one service": a Catalog change that also edits Ordering is inside Class
A's map, which is why the declared touch set is the second check. A `+`-joined
class is the union of its members' maps.

The body carries exactly one row of each, or the run is refused with exit 2
rather than judged. Half the metadata makes half the gate impossible, so a
missing row, a repeated one, a class letter outside A-E, a repeated member or
prose where a path list should be is a refusal naming the row, never its
content.

The map is read by a parser that accepts the one shape its header states,
because there is no stdlib YAML parser and a gate that needs a `pip install`
gets skipped. A line outside that shape, a missing class, a repeated class or
a class with no items refuses the whole map: a half-read map is a gate reading
a file other than the one a reader sees.

The glob dialect and the row grammar are `pr-locality.sh`'s. `**` crosses
directories, `*` and `?` do not, `{a,b}` is an alternation, a token also
covers everything beneath the directory it names, and every token is
repository-relative: no leading `/`, no `./`, no `..` segment, brace
alternatives included. The harness helper cannot run Python under its grant,
and CI fetches its own payload, `changedFiles` and the files endpoint included,
rather than the helper's field set, so the grammar has two implementations,
which must accept and refuse the same tokens.

The touch-set cell is never printed, because a PR author is not a trusted
party. A changed path is the author's text too, since git permits a newline
inside a name, so each must be a plain path and one that is not refuses the
run: a verdict list with a line withheld reads as complete.

The file list can be short in two ways that its own paths do not show. The
files endpoint returns a bounded number of entries however it is paginated, so
the payload carries GitHub's `changedFiles` count and a shorter list is
refused. A rename arrives as one entry with the source in `previous_filename`,
so both ends of a rename are judged.

Stdlib only, on the licence gate's terms. The deciding takes JSON on stdin and
the fetching is two `gh` calls in the workflow, `deploy/canary/canary.py`'s
split. A file entry is a plain path or the endpoint's own
`{filename, previous_filename}`.

    {"number": 190, "body": "<the PR body>", "changedFiles": 2,
     "files": [{"filename": "<path>", "previous_filename": null}, ...]}

    gh api "repos/{owner}/{repo}/pulls/<n>/files" --paginate --jq '.[] | {filename, previous_filename}' |
        jq -s --argjson pr "$(gh pr view <n> --json number,body,changedFiles)" '$pr + {files: .}' |
        py -3.12 .github/locality-gate/locality_gate.py
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections.abc import Callable
from pathlib import Path

DEFAULT_MAP = Path(__file__).resolve().parent / "classes.yml"

CLASSES = "ABCDE"

# A touch-set token: path and glob characters, and nothing that is not one.
_TOKEN = re.compile(r"^[A-Za-z0-9_./*?{},()-]+$")
# A changed path, as the diff names it. Wider than a token by `@` and `+`,
# narrower by every glob character; the same set pr-locality.sh admits.
_PLAIN_PATH = re.compile(r"^[A-Za-z0-9_./@+()-]+$")

_CLASS_ROW = re.compile(r"^\| *Class *\|(.*)$")
_TOUCH_ROW = re.compile(r"^\| *Touch set *\|(.*)$")
_MAP_CLASS = re.compile(r"^([A-E]):\s*$")
_MAP_ITEM = re.compile(r"^  - '([^']+)'\s*$")


class InputRefused(Exception):
    """The input is not something this gate can judge, and says why."""


# --------------------------------------------------------------------------
# the map
# --------------------------------------------------------------------------

def read_map(text: str) -> dict[str, list[str]]:
    """The class -> tokens map, from the one shape `classes.yml` may take."""
    found: dict[str, list[str]] = {}
    current: str | None = None
    for number, raw in enumerate(text.splitlines(), start=1):
        line = raw.rstrip()
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        klass = _MAP_CLASS.match(line)
        if klass:
            name = klass.group(1)
            if name in found:
                raise InputRefused(f"classes.yml names class {name} twice (line {number})")
            found[name] = []
            current = name
            continue
        item = _MAP_ITEM.match(line)
        if item and current is not None:
            token = _normalise_token(item.group(1), where=f"classes.yml line {number}")
            found[current].append(token)
            continue
        raise InputRefused(
            f"classes.yml line {number} is outside the map's grammar: a class line is "
            f"`A:` to `E:`, an item line is two spaces, a dash, a space and one "
            f"single-quoted path token, and anything else is a comment"
        )
    for name in CLASSES:
        if name not in found:
            raise InputRefused(f"classes.yml has no entry for class {name}")
        if not found[name]:
            raise InputRefused(f"classes.yml class {name} has no items")
    return found


# --------------------------------------------------------------------------
# the rows
# --------------------------------------------------------------------------

def read_rows(body: str) -> tuple[list[str], list[str]]:
    """The class members and the touch-set tokens the body declares.

    Exactly one row of each. The refusal messages name the row and never its
    content, for the reason the module docstring gives.
    """
    class_rows = [m.group(1) for m in map(_CLASS_ROW.match, body.splitlines()) if m]
    touch_rows = [m.group(1) for m in map(_TOUCH_ROW.match, body.splitlines()) if m]
    if len(class_rows) > 1:
        raise InputRefused("the body carries more than one `| Class |` row")
    if len(touch_rows) > 1:
        raise InputRefused("the body carries more than one `| Touch set |` row")
    if not class_rows:
        raise InputRefused(
            "the body carries no `| Class |` row; docs/change-locality.md section 5 asks "
            "the PR body to name the class and the touch set"
        )
    if not touch_rows:
        raise InputRefused(
            "the body carries no `| Touch set |` row; docs/change-locality.md section 5 asks "
            "the PR body to name the class and the touch set"
        )
    return _read_class(class_rows[0]), _read_touch_set(touch_rows[0])


def _cell(rest: str, row: str) -> str:
    # The text between the second `|` and the closing one, and one cell only.
    if not rest.rstrip().endswith("|"):
        raise InputRefused(f"the {row} row is not one cell")
    cell = rest.rstrip()[:-1].strip()
    if "|" in cell:
        raise InputRefused(f"the {row} row is not one cell")
    return cell


def _read_class(rest: str) -> list[str]:
    cell = _cell(rest, "Class")
    # A+D+E is the one three-member class, and it is spelled only that way:
    # docs/change-locality.md section 3 argues why a service's own work is
    # the case that needs it.
    if not re.fullmatch(r"[A-E](\+[A-E])?|A\+D\+E", cell):
        raise InputRefused(
            "the Class row is not a class: one letter A-E, two distinct letters joined by `+`, or `A+D+E`")
    members = cell.split("+")
    if len(set(members)) != len(members):
        raise InputRefused("the Class row repeats a class")
    return members


def _read_touch_set(rest: str) -> list[str]:
    cell = _cell(rest, "Touch set")
    if not cell:
        raise InputRefused("the Touch set row is empty")
    tokens = []
    for item in _split_outside_braces(cell):
        token = item.strip()
        if token.startswith("`") and token.endswith("`") and len(token) >= 2:
            token = token[1:-1]
        elif "`" in token:
            raise InputRefused("the Touch set row has an unbalanced backtick")
        tokens.append(_normalise_token(token, where="the Touch set row"))
    return tokens


def _split_outside_braces(cell: str) -> list[str]:
    items, current, depth = [], "", 0
    for character in cell:
        if character == "{":
            depth += 1
        elif character == "}":
            depth -= 1
            if depth < 0:
                raise InputRefused("the Touch set row has an unbalanced brace")
        elif character == "," and depth == 0:
            items.append(current)
            current = ""
            continue
        current += character
    if depth != 0:
        raise InputRefused("the Touch set row has an unbalanced brace")
    items.append(current)
    return items


def _normalise_token(token: str, *, where: str) -> str:
    """One path-or-glob token, checked and with a trailing slash dropped."""
    # fullmatch, because `$` matches before a final newline and a token
    # ending in one would pass as its newline-free spelling.
    if not _TOKEN.fullmatch(token) or not re.search(r"[/.]", token):
        raise InputRefused(f"{where} is not a path list")
    # Braces are walked, not counted. A count accepts `docs/}a{` and, worse,
    # `docs/a,docs/b` — a comma outside any brace, which the touch-set row
    # can never carry because it is split on that comma first, but which a
    # single-quoted map item hands straight here. `matcher()` turns that
    # comma into an ungrouped `|`, and `^docs/a|docs/b(/.*)?$` matches
    # `docs/admin` — a malformed map silently widening a class rather than
    # being refused.
    depth = 0
    for character in token:
        if character == "{":
            depth += 1
        elif character == "}":
            depth -= 1
            if depth < 0:
                raise InputRefused(f"{where} has an unbalanced brace")
        elif character == "," and depth == 0:
            raise InputRefused(f"{where} has a comma outside a brace alternation")
    if depth != 0:
        raise InputRefused(f"{where} has an unbalanced brace")
    # At most one trailing slash, as pr-locality.sh drops one with `${t%/}`:
    # `docs/` names the directory, and `docs//` keeps an empty segment for
    # the boundary check below to refuse. Stripping every slash would read
    # the malformed token as the whole tree.
    if token.endswith("/"):
        token = token[:-1]
    # A brace alternative is a segment start too: `{../outside,docs/x.md}`
    # expands to a path that leaves the checkout, so the boundary is judged
    # over the token with its braces dropped and its alternatives joined as
    # segments, where a leading `/`, a `./` and a `..` all show as segments.
    joined = token.replace("{", "").replace("}", "").replace(",", "/")
    if re.search(r"(^|/)(\.{1,2})?(/|$)", joined):
        raise InputRefused(f"{where} names a path outside the repository")
    return token


# --------------------------------------------------------------------------
# matching
# --------------------------------------------------------------------------

def matcher(token: str) -> Callable[[str], bool]:
    """The token as an anchored predicate over a repository-relative path.

    `**` crosses directories, `*` and `?` do not, braces are alternation, and
    a token also covers everything beneath the directory it names.
    """
    if token.endswith("/"):
        token = token[:-1]
    out = []
    index = 0
    while index < len(token):
        character = token[index]
        if token.startswith("**", index):
            out.append(".*")
            index += 2
            continue
        if character == "*":
            out.append("[^/]*")
        elif character == "?":
            out.append("[^/]")
        elif character == "{":
            out.append("(")
        elif character == "}":
            out.append(")")
        elif character == ",":
            out.append("|")
        else:
            out.append(re.escape(character))
        index += 1
    pattern = re.compile("^" + "".join(out) + "(/.*)?$")
    return lambda path: pattern.match(path) is not None


def _plain_path(entry: object) -> str:
    # fullmatch rather than match: `$` matches before a final newline, and
    # git permits a name ending in one, so `docs/x.md\n` would otherwise pass
    # as `docs/x.md` and be judged as the path it is not.
    if not isinstance(entry, str) or not _PLAIN_PATH.fullmatch(entry) or not re.search(r"[/.]", entry):
        raise InputRefused("a changed path is not a plain path, so the run is refused rather than judged short")
    if re.search(r"(^|/)(\.{1,2})?(/|$)", entry):
        raise InputRefused("a changed path is not a plain path, so the run is refused rather than judged short")
    return entry


# --------------------------------------------------------------------------
# the verdict
# --------------------------------------------------------------------------

def _names(entry: object) -> list[str]:
    """The paths one file entry names: a string is one path, and an object is
    the endpoint's own shape, where a rename carries the source as
    `previous_filename` beside the destination. Both ends are judged, because
    a move out of `src/` removes a code path whatever the destination is."""
    if isinstance(entry, str):
        return [entry]
    if isinstance(entry, dict) and isinstance(entry.get("filename"), str):
        previous = entry.get("previous_filename")
        return [entry["filename"]] + ([previous] if isinstance(previous, str) and previous else [])
    raise InputRefused("a changed path is not a plain path, so the run is refused rather than judged short")


def check(payload: dict, class_map: dict[str, list[str]]) -> list[str]:
    """Every problem with the diff against both sets; empty when it passes."""
    if not isinstance(payload.get("body"), str):
        raise InputRefused("the payload carries no `body`")
    if not isinstance(payload.get("files"), list):
        raise InputRefused("the payload carries no `files` list")
    members, declared = read_rows(payload["body"])
    entries = payload["files"]
    if not entries:
        raise InputRefused("the pull request has no changed files, which is an empty subject rather than a pass")
    # The files endpoint returns at most 3,000 entries however it is paginated,
    # so a longer pull request hands this gate a non-empty prefix that looks
    # exactly like a complete list. `changedFiles` is GitHub's own count of the
    # same list, and a list shorter than it is refused rather than judged. The
    # count is required, not optional: a payload without it is a workflow
    # that stopped sending it, and judging the list anyway is the fail-open
    # the field exists to close.
    expected = payload.get("changedFiles")
    if not isinstance(expected, int) or isinstance(expected, bool):
        raise InputRefused(
            "the payload carries no numeric `changedFiles`; without GitHub's own count a file list "
            "cannot be told from a prefix of one, so it is refused rather than judged"
        )
    if expected != len(entries):
        raise InputRefused(
            f"the file list carries {len(entries)} entries against the pull request's changedFiles "
            f"of {expected}; the files endpoint returns at most 3,000, so a shorter list is a prefix "
            f"and is refused rather than judged"
        )
    paths = [_plain_path(name) for entry in entries for name in _names(entry)]

    allowed = [matcher(token) for member in members for token in class_map[member]]
    own = [matcher(token) for token in declared]
    label = "+".join(members)
    problems = []
    for path in paths:
        if not any(match(path) for match in allowed):
            problems.append(
                f"`{path}` is outside class {label}'s tree set in .github/locality-gate/classes.yml; "
                f"docs/change-locality.md section 3: the class is wrong, so change the class, not the set"
            )
        if not any(match(path) for match in own):
            problems.append(
                f"`{path}` is outside the declared touch set; docs/change-locality.md section 3: "
                f"narrow the diff, or add the path to the `| Touch set |` row with its reason beside it"
            )
    return problems


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("payload", nargs="?", type=argparse.FileType(encoding="utf-8"), default=sys.stdin,
                        help="JSON with `number`, `body` and `files`; stdin by default")
    parser.add_argument("--map", type=Path, default=DEFAULT_MAP,
                        help="the class -> tree-set map; classes.yml beside this file by default")
    args = parser.parse_args(argv[1:])

    try:
        payload = json.load(args.payload)
    except json.JSONDecodeError as error:
        print(f"locality-gate: the input is not JSON: {error}", file=sys.stderr)
        return 2
    if not isinstance(payload, dict):
        print("locality-gate: the input is not a JSON object", file=sys.stderr)
        return 2

    try:
        class_map = read_map(args.map.read_text(encoding="utf-8"))
        problems = check(payload, class_map)
    except InputRefused as refusal:
        print(f"locality-gate: refused: {refusal}", file=sys.stderr)
        return 2
    except OSError as error:
        print(f"locality-gate: cannot read classes.yml: {error}", file=sys.stderr)
        return 2

    number = payload.get("number", "?")
    if problems:
        print(f"locality-gate: {len(problems)} problem(s) with where PR #{number}'s diff lands:\n", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    members, _ = read_rows(payload["body"])
    print(
        f"locality-gate: every changed path in PR #{number} is inside class {'+'.join(members)}'s "
        f"tree set and inside the declared touch set."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
