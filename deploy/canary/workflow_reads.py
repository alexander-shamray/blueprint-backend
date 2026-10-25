"""How deploy.yml and deploy/helm/smoke.sh read the descriptors: the smoke cases a
descriptor owes, the dispatch menu and the descriptors each reader names."""

from __future__ import annotations

import re
from pathlib import Path

from descriptor_model import PlanError, ROOT, entries
from release_inputs import RELEASE_NAME


WORKFLOW_PATH = ".github/workflows/deploy.yml"

WORKFLOW = ROOT / WORKFLOW_PATH


SMOKE_PATH = "deploy/helm/smoke.sh"

SMOKE = ROOT / SMOKE_PATH

# The reads of the descriptors, as whole live lines: the rollout's guard, whose
# output names no input, and the line smoke.sh takes its cases from.
DISPATCH_GUARD = re.compile(
    r'(?m)^[ \t]*if ! python deploy/canary/canary\.py chart --workload="\$WORKLOAD" >/dev/null 2>&1; then\n'
    r'[ \t]*echo "::error::[^"$`\\]*"\n[ \t]*exit 1\n[ \t]*fi$')

SMOKE_CASES_READ = re.compile(
    r"""(?m)^[ \t]*\$PYTHON "\$ROOT/deploy/canary/canary\.py" smoke-cases \| tr -d '\\r' >"\$CASES"$""")

# The hand-written chart lists check 8 finds in smoke.sh: a plain or declared
# assignment to a `*CHARTS` name, and a `for` loop's word list. The forms it
# misses are the README's, under "A deployable is one descriptor".
CHARTS_ASSIGNED = re.compile(
    r"(?im)(?:^|[\s;&|])(?:(?:readonly|declare|typeset|local|export)(?:[ \t]+-\w+)*[ \t]+)?\w*charts\+?=")

FOR_LIST = re.compile(r"(?m)(?:^|[\s;&|])for[ \t]+\w+[ \t]+in[ \t]")

# What smoke.sh splits a case into words by, so no value may hold a space or a
# character a shell acts on in an expansion's result; braces are not one, since
# bash expands them before it substitutes. Nor a comma, which Helm reads as a
# second assignment outside braces and which this refuses everywhere.
SOURCE_PATH = re.compile(r"src(/[A-Za-z0-9_-][A-Za-z0-9._-]*)+")

HOST_NAME = re.compile(r"[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*")

CAPABILITY = re.compile(r"[A-Za-z]+")

OVERLAY = re.compile(r"[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+=[A-Za-z0-9._:/@{}-]*")


def _live(text: str) -> str:
    """The text less its comments, whole-line and trailing, which argue about what runs."""
    return "\n".join(_uncommented(line) for line in text.splitlines() if not line.lstrip().startswith("#"))


def _uncommented(line: str) -> str:
    """One line less a trailing comment: a `#` that starts a word outside quotes."""
    quote = None
    index = 0
    while index < len(line):
        char = line[index]
        if quote == "'":
            quote = None if char == "'" else quote
        elif char == "\\":
            index += 1
        elif quote == '"':
            quote = None if char == '"' else quote
        elif char in "'\"":
            quote = char
        elif char == "#" and (index == 0 or line[index - 1] in " \t;&|()"):
            return line[:index].rstrip()
        index += 1
    return line.rstrip()


def _shell_words(text: str, start: int, stops: str) -> tuple[str, list[str]]:
    """The shell value at `start` as written, and its literal words.

    A word holding an expansion is not literal, and a command substitution's
    contents are not the value's words; an array value runs to its `)`.
    """
    stack: list[str] = []
    outside: list[str] = []
    index = start
    if text.startswith("(", index):
        stack.append("array")
        index += 1
    while index < len(text):
        char, top = text[index], (stack[-1] if stack else None)
        if top == "'":
            if char == "'":
                stack.pop()
        elif char == "\\":
            if "$(" not in stack:
                outside.append(text[index:index + 2])
            index += 2
            continue
        elif top == '"' and char == '"':
            stack.pop()
        elif text.startswith("$(", index):
            stack.append("$(")
            index += 2
            continue
        elif top != '"' and char in "'\"":
            stack.append(char)
        elif top == "$(" and char == "(":
            stack.append("$(")
        elif char == ")" and top in ("$(", "array"):
            stack.pop()
            index += 1
            if top == "array" and not stack:
                break
            continue
        elif not stack and char in stops:
            break
        if "$(" not in stack:
            outside.append(char)
        index += 1
    words = [word.replace('"', "").replace("'", "").replace("\\", "") for word in "".join(outside).split()]
    return text[start:index], [word for word in words if word and "$" not in word]


def _hand_lists(text: str, charts: set[str]) -> list[str]:
    """Each hand-written chart list in smoke.sh that check 8 finds, as written; the README names those it misses."""
    found = []
    for match in CHARTS_ASSIGNED.finditer(text):
        value, words = _shell_words(text, match.end(), " \t\n;&|")
        if words:
            found.append(match.group(0).lstrip(" \t\n;&|") + value)
    for match in FOR_LIST.finditer(text):
        value, words = _shell_words(text, match.end(), "\n;&|")
        if {name for word in words for name in _braces_expanded(word)} & charts:
            found.append(match.group(0).lstrip(" \t\n;&|") + value)
    return found


def _braces_expanded(word: str) -> list[str]:
    """The words bash makes of `word` by expanding its unnested `{a,b}` lists."""
    brace = re.search(r"\{([^{},]*(?:,[^{},]*)+)\}", word)
    if not brace:
        return [word]
    head, tail = word[:brace.start()], word[brace.end():]
    return [name for item in brace.group(1).split(",") for name in _braces_expanded(head + item + tail)]


def _smoke_case(name: str, entry: dict) -> list[str]:
    """One descriptor as `smoke-cases` prints it, or why smoke.sh cannot."""
    chart, source, smoke = entry.get("chart"), entry.get("source"), entry.get("smoke")
    if not isinstance(smoke, dict):
        raise PlanError(
            f"workloads.{name} has no smoke block, so smoke.sh cannot say which "
            f"of its cases hold for the chart {chart!r}"
        )
    if not isinstance(chart, str) or not RELEASE_NAME.fullmatch(chart):
        raise PlanError(f"workloads.{name}.chart is {chart!r}, which smoke.sh cannot read as one word")
    if not isinstance(source, str) or not SOURCE_PATH.fullmatch(source):
        raise PlanError(
            f"workloads.{name}.source is {source!r}, which is not a path under src/ "
            "that smoke.sh can read as one word"
        )
    host = entry.get("serviceName")
    if not isinstance(host, str) or not HOST_NAME.fullmatch(host):
        raise PlanError(
            f"workloads.{name}.serviceName is {host!r}, which smoke.sh cannot read as one "
            "project name to find the host's own settings by"
        )
    lines = [f"{chart} release {name}", f"{chart} source {source}", f"{chart} host {host}"]
    for key in ("migrator", "database", "autoscaled"):
        if not isinstance(smoke.get(key), bool):
            raise PlanError(
                f"workloads.{name}.smoke.{key} is {smoke.get(key)!r} rather than "
                f"true or false, so smoke.sh cannot classify the chart {chart!r}"
            )
        lines.append(f"{chart} {key} {'yes' if smoke[key] else 'no'}")
    # ADR-153's release off the ladder renders no canary and no disruption budget, which smoke.sh asks of the rest.
    lines.append(f"{chart} ladder {'no' if 'offLadder' in entry else 'yes'}")
    if smoke["migrator"] and not smoke["database"]:
        raise PlanError(
            f"workloads.{name}.smoke says the chart {chart!r} migrates a database "
            "it does not have, so smoke.sh would hold it to two classes at once"
        )
    for key, field, pattern in (("capabilities", "capability", CAPABILITY), ("overlay", "overlay", OVERLAY)):
        values = smoke.get(key)
        if not isinstance(values, list) or not all(
                isinstance(value, str) and pattern.fullmatch(value) for value in values):
            raise PlanError(
                f"workloads.{name}.smoke.{key} is {values!r}, which is not a list "
                "of words smoke.sh can hand to helm"
            )
        lines += [f"{chart} {field} {value}" for value in values]
    return lines


def smoke_cases(workloads: dict) -> list[str]:
    """Every descriptor's smoke.sh cases, as `<chart> <field> <value>` lines.

    A chart migrated by another names that chart, so smoke.sh can hold the two
    to one database; an unknown name is check 4's to refuse and prints nothing.
    """
    workloads = entries(workloads)
    lines = []
    for name, entry in workloads.items():
        lines += _smoke_case(name, entry)
        named = entry.get("migratedBy")
        migrator = workloads.get(named) if isinstance(named, str) else None
        if isinstance(migrator, dict) and isinstance(migrator.get("chart"), str):
            lines.append(f"{entry['chart']} migratedBy {migrator['chart']}")
    return lines


def _smoke_reads(name: str, entry: dict, root: Path) -> list[str]:
    """Check 4 for smoke.sh: the cases parse, and the source holds the host."""
    try:
        _smoke_case(name, entry)
    except PlanError as error:
        return [str(error)]
    host = f"{entry.get('serviceName')}.csproj"
    if not any((root / entry["source"]).rglob(host)):
        return [
            f"workloads.{name}.source is {entry['source']!r}, which holds no {host}, "
            "so smoke.sh would hold the chart to another host's code"
        ]
    return []


def _dispatch_options(text: str) -> set[str] | None:
    """The names in every `workload:` key's menu, or None where no key has one.

    A key's own `options:` or `type: choice`, blank lines aside, or either in a
    flow mapping on its key line, is a menu; a sibling's or a description's is not.
    """
    text = "\n".join(line for line in text.splitlines() if line.strip()) + "\n"
    menus = []
    for flow in re.finditer(r"(?m)^[ \t]*workload:[ \t]*(\{.*)$", text):
        # A quoted scalar with a colon may fake a key, so it is blanked; one without may be a key or 'choice'.
        mapping = re.sub(r"""'[^']*'|"(?:[^"\\]|\\.)*\"""", lambda q: "''" if ":" in q.group(0) else q.group(0),
                         flow.group(1))
        if re.search(r"""\boptions["']?[ \t]*:|\btype["']?[ \t]*:[ \t]*["']?choice\b""", mapping):
            listed = re.search(r"""\boptions["']?[ \t]*:[ \t]*\[([^\]]*)\]""", mapping)
            parts = listed.group(1).split(",") if listed else []
            menus.append({item for item in (part.strip().strip("'\"") for part in parts) if item})
    for block in re.finditer(r"(?m)^([ \t]*)workload:\n((?:\1[ \t].*\n?)*)", text):
        child_indent = re.match(r"[ \t]+", block.group(2))
        if not child_indent:
            continue
        own, body = re.escape(child_indent.group(0)), block.group(2)
        options = re.search(rf"(?m)^{own}options:(.*(?:\n{own}(?:[ \t]|-[ \t]).*)*)", body)
        choice = re.search(rf"""(?m)^{own}type:[ \t]*(["']?)choice\1[ \t]*$""", body)
        if options or choice:
            parts = re.split(r"[\[\],\n]", options.group(1) if options else "")
            items = (re.sub(r"^-[ \t]+", "", part.strip()).strip("'\"") for part in parts)
            menus.append({item for item in items if item})
    return set().union(*menus) if menus else None


def _reader_texts(workflow: Path, smoke: Path) -> tuple[str, str]:
    """The workflow's and smoke.sh's live text, or why one cannot be read."""
    try:
        return _live(workflow.read_text(encoding="utf-8")), _live(smoke.read_text(encoding="utf-8"))
    except OSError as error:
        raise PlanError(f"a descriptor reader is not readable: {error}") from error


def _charts(workloads: dict) -> set[str]:
    return {entry.get("chart") for entry in entries(workloads).values()}


def descriptors_read(workloads: dict, workflow: Path = WORKFLOW, smoke: Path = SMOKE) -> dict[str, set[str]]:
    """The descriptors each reader takes in, from the text it runs."""
    workflow_text, smoke_text = _reader_texts(workflow, smoke)

    described = set(entries(workloads))
    rolled = set(_dispatch_options(workflow_text) or ())
    if DISPATCH_GUARD.search(workflow_text):
        rolled |= described
    rendered = set()
    if SMOKE_CASES_READ.search(smoke_text) and not _hand_lists(smoke_text, _charts(workloads)):
        for name in described:
            try:
                _smoke_case(name, workloads[name])
                rendered.add(name)
            except PlanError:
                pass
    return {"workflow": rolled, "canary": described, "smoke": rendered}


def _descriptors_agree(workloads: dict, workflow: Path = WORKFLOW, smoke: Path = SMOKE) -> list[str]:
    """Check 8: the workflow reads the descriptors; smoke.sh each it rolls."""
    try:
        read = descriptors_read(workloads, workflow, smoke)
        workflow_text, smoke_text = _reader_texts(workflow, smoke)
    except PlanError as error:
        return [f"the descriptor readers cannot be compared: {error}"]

    failures = []
    menu = _dispatch_options(workflow_text)
    if menu is not None:
        named = f" ({', '.join(sorted(menu))})" if menu else ""
        failures.append(
            f"{WORKFLOW_PATH} lists its workloads by hand{named}: the workload input's "
            "`options:` or `type: choice` is a menu, a second copy of the descriptors "
            "under deploy/canary/deployables"
        )
    if not DISPATCH_GUARD.search(workflow_text):
        failures.append(
            f"{WORKFLOW_PATH} holds no live guard that stops a dispatch `canary.py chart` "
            "cannot look up, so a dispatch is checked against nothing"
        )
    if not SMOKE_CASES_READ.search(smoke_text):
        failures.append(f"{SMOKE_PATH} takes no live cases from `canary.py smoke-cases`")
    for listed in _hand_lists(smoke_text, _charts(workloads)):
        failures.append(
            f"{SMOKE_PATH} lists charts by hand in {listed!r}, a second copy of the "
            "descriptors under deploy/canary/deployables"
        )
    for reader in ("canary", "smoke"):
        missing = read["workflow"] - read[reader]
        if missing:
            failures.append(
                f"{WORKFLOW_PATH} can roll {', '.join(sorted(missing))}, which "
                f"{'no descriptor describes' if reader == 'canary' else SMOKE_PATH + ' does not render'}"
            )
    return failures
