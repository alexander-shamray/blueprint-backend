#!/usr/bin/env python3
"""Fail the build on an issue form whose choices have drifted from the vocabularies and labels they stand for.

The repository's labels arrive on stdin as `gh label list --json name`; the README owns the claim.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

DEFAULT_TEMPLATES = REPO_ROOT / ".github" / "ISSUE_TEMPLATE"
DEFAULT_HELPER = REPO_ROOT / ".claude" / "scripts" / "gh-issue-create.sh"
DEFAULT_CONTRACT = REPO_ROOT / "docs" / "change-locality.md"

# Every dropdown a form carries is one of these, and every form carries all three.
GOVERNED = ("kind", "severity", "area")

# The bullet of the contract's section 6 that owns the issue vocabulary.
VOCABULARY_BULLET = "**The issue vocabulary is wider than the label helper**"

FIELD = re.compile(r"^  - type: (\S+)\s*$")
FIELD_ID = re.compile(r"^    id: (\S+)\s*$")
OPTIONS = re.compile(r"^      options:\s*$")
OPTION = re.compile(r"^        - (.*?)\s*$")
LABEL_NAME = re.compile(r"^[a-z0-9][a-z0-9-]*$")
BLANK_OFF = re.compile(r"^blank_issues_enabled:\s*false\s*$", re.MULTILINE)
# A top-level `labels:` attaches its labels to every issue the form opens, whatever was answered.
FIXED_LABELS = re.compile(r"""^["']?labels["']?\s*:""", re.MULTILINE)
WORD = re.compile(r"`([a-z0-9-]+)`")


class InputRefused(Exception):
    pass


def dropdowns(form: Path) -> dict[str, list[str]]:
    """The id -> options of each dropdown, from the one shape finding.yml keeps."""
    found: dict[str, list[str]] = {}
    field_type: str | None = None
    field_id: str | None = None
    options: list[str] = []
    collecting = False
    lines = form.read_text(encoding="utf-8").splitlines()

    def close() -> None:
        if field_type != "dropdown":
            return
        if field_id is None:
            raise InputRefused(f"{form.name}: a dropdown has no id")
        if not options:
            raise InputRefused(f"{form.name}: dropdown {field_id} has no options")
        if field_id in found:
            raise InputRefused(f"{form.name}: dropdown {field_id} appears twice")
        found[field_id] = options

    for number, line in enumerate(lines, start=1):
        if collecting:
            # A blank line does not end the list, or the options after it would go unread.
            if not line.strip():
                continue
            if match := OPTION.match(line):
                if not LABEL_NAME.match(match.group(1)):
                    raise InputRefused(
                        f"{form.name} line {number}: option {match.group(1)!r} is not a bare label name")
                options.append(match.group(1))
                continue
            if line.startswith("        "):
                raise InputRefused(f"{form.name} line {number} is inside an options list but is not an option")
            collecting = False
        if match := FIELD.match(line):
            close()
            field_type, field_id, options = match.group(1), None, []
        elif match := FIELD_ID.match(line):
            field_id = match.group(1)
        elif OPTIONS.match(line):
            if field_type != "dropdown":
                raise InputRefused(f"{form.name} line {number}: options outside a dropdown")
            collecting = True
    close()
    return found


def case_arm(helper: Path, variable: str) -> list[str]:
    """The words of the one arm that admits a value in the helper's `case "$<variable>" in`."""
    text = helper.read_text(encoding="utf-8")
    match = re.search(rf'case "\${variable}" in\s*\n\s*([a-z0-9|,-]+)\) ;;', text)
    if match is None:
        raise InputRefused(f"{helper.name} has no `case \"${variable}\"` arm of the shape this gate reads")
    return match.group(1).split("|")


def contract_kinds(contract: Path) -> list[str]:
    """The kinds the contract's vocabulary bullet names, in its `kind is ...` sentence."""
    text = contract.read_text(encoding="utf-8")
    start = text.find(VOCABULARY_BULLET)
    if start < 0:
        raise InputRefused(f"{contract.name} has no bullet opening {VOCABULARY_BULLET}")
    end = text.find("\n- ", start)
    bullet = " ".join(text[start:end if end >= 0 else len(text)].split())
    match = re.search(r"kind is ((?:`[a-z0-9-]+`(?:, | or )?)+)", bullet)
    if match is None:
        raise InputRefused(f"{contract.name}'s vocabulary bullet has no `kind is ...` list")
    return WORD.findall(match.group(1))


def label_names(stdin: str) -> set[str]:
    try:
        rows = json.loads(stdin)
    except json.JSONDecodeError as error:
        raise InputRefused(f"stdin is not JSON: {error}") from error
    if not isinstance(rows, list) or not rows:
        raise InputRefused("stdin holds no label, so no label was read")
    try:
        return {row["name"] for row in rows}
    except (TypeError, KeyError) as error:
        raise InputRefused("stdin is not a list of objects with a name") from error


def same(what: str, have: list[str], want: list[str], owner: str) -> list[str]:
    problems = []
    if len(set(have)) != len(have):
        problems.append(f"{what} repeats an option: {', '.join(have)}")
    if set(have) != set(want):
        missing = sorted(set(want) - set(have))
        extra = sorted(set(have) - set(want))
        problems.append(f"{what} differs from {owner}: missing {missing or 'nothing'}, extra {extra or 'nothing'}")
    return problems


def check(templates: Path, helper: Path, contract: Path, labels: set[str]) -> list[str]:
    if not templates.is_dir():
        return [f"{templates} is not a directory, so no form was read"]
    config = templates / "config.yml"
    problems: list[str] = []
    if not config.is_file() or not BLANK_OFF.search(config.read_text(encoding="utf-8")):
        problems.append(f"{config.name} does not set blank_issues_enabled: false")

    entries = sorted(p for p in templates.iterdir() if p.name != config.name)
    forms = [p for p in entries if p.is_file() and p.suffix in (".yml", ".yaml")]
    # GitHub offers a Markdown template beside the form, with none of the fields the form asks for.
    for other in sorted(set(entries) - set(forms)):
        problems.append(f"{other.name} is neither config.yml nor a form")
    if not forms:
        return problems + [f"{templates} holds no issue form, so no form was read"]

    kinds = contract_kinds(contract)
    helper_kinds = case_arm(helper, "kind")
    severities = case_arm(helper, "severity")
    # An area alternative such as `code,test` is one `--label` per word, and the form offers the words.
    areas = sorted({word for alternative in case_arm(helper, "area") for word in alternative.split(",")})
    outside = sorted(set(helper_kinds) - set(kinds))
    if outside:
        problems.append(f"{helper.name} files under kinds the contract's vocabulary lacks: {outside}")

    for form in forms:
        if FIXED_LABELS.search(form.read_text(encoding="utf-8")):
            problems.append(
                f"{form.name} carries a top-level labels key, which attaches labels no answer chose")
        found = dropdowns(form)
        for name in sorted(set(found) - set(GOVERNED)):
            problems.append(f"{form.name}: dropdown {name} is not one of {', '.join(GOVERNED)}")
        for name in GOVERNED:
            if name not in found:
                problems.append(f"{form.name}: no {name} dropdown")
        if "kind" in found:
            problems += same(f"{form.name}'s kind", found["kind"], kinds, f"{contract.name} section 6")
        if "severity" in found:
            problems += same(f"{form.name}'s severity", found["severity"], severities, helper.name)
        if "area" in found:
            problems += same(f"{form.name}'s area", found["area"], areas, helper.name)
        for name, options in found.items():
            for option in options:
                if option not in labels:
                    problems.append(f"{form.name}: {name} option {option} is not a label in this repository")
    return problems


def main(argv: list[str] | None = None, stdin: str | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--templates", type=Path, default=DEFAULT_TEMPLATES)
    parser.add_argument("--helper", type=Path, default=DEFAULT_HELPER)
    parser.add_argument("--contract", type=Path, default=DEFAULT_CONTRACT)
    args = parser.parse_args(argv)

    try:
        labels = label_names(sys.stdin.read() if stdin is None else stdin)
        problems = check(args.templates, args.helper, args.contract, labels)
    except InputRefused as error:
        problems = [str(error)]
    for problem in problems:
        print(f"issue-form-gate: {problem}", file=sys.stderr)
    if problems:
        return 1
    print("issue-form-gate: every form's choices match their vocabularies and are labels in this repository")
    return 0


if __name__ == "__main__":
    sys.exit(main())
