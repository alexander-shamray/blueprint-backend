"""What deploy.yml's rollout steps must do with the facts the descriptors give it
(ADR-050, ADR-118, ADR-122, ADR-125, ADR-126), found by reading the workflow."""

from __future__ import annotations

import re
from pathlib import Path

from workflow_reads import WORKFLOW, WORKFLOW_PATH


# Where the rollout puts the image's own `src/`, and the flag that carries it.
# Named here because check 12 asserts the workflow uses both, and a gate whose
# subject is spelled twice is a gate that stops agreeing with itself.
IMAGE_SOURCE = "IMAGE_SOURCE"

INSTALLED_SOURCE = "INSTALLED_SOURCE"

SOURCE_FLAG = "--source"

INSTALLED_FLAG = "--installed-source"

# The revision the release that migrates the rolled workload's schema serves,
# which check 13 asserts the rollout holds the tag's revision to (ADR-118).
MIGRATED_REVISION = "MIGRATED_REVISION"

# The other direction (ADR-122): the revision the rolled workload's stable
# track serves, and the one a host whose schema it migrates serves, which
# check 13 asserts the rollout holds to it.
STABLE_REVISION = "STABLE_REVISION"

HOST_REVISION = "HOST_REVISION"


def _argument(flag: str, variable: str) -> str:
    """The whole argument check 12 looks for, spelled once.

    Whole, because a flag and a name found anywhere on the line also match
    `--source "$OLD_IMAGE_SOURCE"`, which passes a different tree.
    """
    return f'{flag} "${variable}"'


# What an archive writes and what a step exports, so check 12 can ask
# whether they are the same path. A tree exported by the right name and
# taken from somewhere else reads the checkout while every name matches.
ARCHIVED = re.compile(
    r'git\s+archive\s+"?(?P<revision>[^"\s]+)"?\s+src\b[^\n]*?-C\s+"?(?P<into>[^"\s]+)"?')

# The revision every archive has to be of, and the assignment that has to
# have produced it. Named rather than denied: a list of spellings that are
# not the right one admits the next spelling nobody listed, which is the
# enumeration failure this repository keeps re-finding.
RESOLVED_REVISION = "$REVISION"

ASSIGNS_REVISION = re.compile(r"(?<![A-Za-z0-9_])REVISION=")

RESOLVES_REVISION = re.compile(
    r"(?<![A-Za-z0-9_])REVISION=\$\(\s*python\s+\S*canary\.py\s+revision"
    r'\s+--value\s+"\$TAG"')

# And the tag that feeds it. The candidate's arrives as a step input, so
# the only shell assignment of it is the stable track's, which has to come
# from the running release rather than from anywhere a name could be put.
ASSIGNS_TAG = re.compile(r"(?<![A-Za-z0-9_])TAG=")

RESOLVES_TAG = re.compile(
    r"(?<![A-Za-z0-9_])TAG=\$\(\s*python\s+\S*canary\.py\s+installed-tag\b")


def _resolved_above(live: list[str], index: int) -> bool:
    """Whether the nearest REVISION= above `index` came from the tag.

    The nearest, because each archive runs in its own step and takes the
    value that step set.
    """
    for line in reversed(live[:index]):
        if ASSIGNS_REVISION.search(line):
            return bool(RESOLVES_REVISION.search(line))
    return False


def _archived(live: list[str]) -> dict[str, tuple[str, bool]]:
    """Each tree an archive writes, its revision, and where that came from."""
    found = {}
    for index, line in enumerate(live):
        match = ARCHIVED.search(line)
        if match:
            found[match.group("into")] = (
                match.group("revision"), _resolved_above(live, index))
    return found


def _exported(live: list[str], variable: str) -> str | None:
    """The path a step writes into GITHUB_ENV under `variable`."""
    pattern = re.compile(
        rf'(?<![A-Za-z0-9_]){variable}=(?P<into>[^"\s]+)"?\s*>>\s*"?\$GITHUB_ENV')
    for line in live:
        match = pattern.search(line)
        if match:
            return match.group("into")
    return None


def _invocations(lines: list[str], command: str) -> list[str]:
    """Each run of `command` in `lines`, with its continuations joined.

    A workflow spells a long command over backslash-continued lines, so the
    check below can ask about a whole invocation rather than a line.
    """
    found = []
    for index, line in enumerate(lines):
        if command not in line:
            continue
        run = line.strip()
        cursor = index
        while run.endswith("\\") and cursor + 1 < len(lines):
            cursor += 1
            run = run[:-1].rstrip() + " " + lines[cursor].strip()
        found.append(run)
    return found


def _rollout_follows_the_migrator(workloads: dict, workflow: Path = WORKFLOW) -> list[str]:
    """Check 13: where a descriptor names `migratedBy`, the rollout asks from both ends and refuses on the answer.

    Only while one does: with none, the steps have nothing to order and their
    absence is not a gap.
    """
    if not any(entry.get("migratedBy") for entry in workloads.values()):
        return []
    try:
        text = workflow.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable, so ADR-118's rollout order is unchecked: {error}"]
    live = [line for line in text.splitlines() if not line.lstrip().startswith("#")]
    asks = _invocations(live, "canary.py migrated-by")
    if not any(_argument("--workload", "WORKLOAD") in run for run in asks):
        return [
            f"{WORKFLOW_PATH} runs no `canary.py migrated-by` for the dispatched "
            f"{_argument('--workload', 'WORKLOAD')}, so a host another release "
            "migrates can roll ahead of its schema (ADR-118)"
        ]
    if not _invocations(live, f'merge-base --is-ancestor "$REVISION" "${MIGRATED_REVISION}"'):
        return [
            f"{WORKFLOW_PATH} asks `canary.py migrated-by` and never holds the tag's "
            f"revision to ${MIGRATED_REVISION}, so the answer refuses nothing (ADR-118)"
        ]
    if not _invocations(live, f'merge-base --is-ancestor "${MIGRATED_REVISION}" "$REVISION"'):
        return [
            f"{WORKFLOW_PATH} holds the tag's revision under ${MIGRATED_REVISION} and not "
            "over it, so a host can roll back to a revision older than the schema "
            "another release has migrated (ADR-122)"
        ]
    asks = _invocations(live, "canary.py migrates ")
    if not any(_argument("--workload", "WORKLOAD") in run for run in asks):
        return [
            f"{WORKFLOW_PATH} runs no `canary.py migrates` for the dispatched "
            f"{_argument('--workload', 'WORKLOAD')}, so a release can migrate a schema "
            "under a host several releases behind it (ADR-122)"
        ]
    if not _invocations(live, f'merge-base --is-ancestor "${STABLE_REVISION}" "${HOST_REVISION}"'):
        return [
            f"{WORKFLOW_PATH} asks `canary.py migrates` and never holds "
            f"${HOST_REVISION} to ${STABLE_REVISION}, so the answer refuses nothing "
            "(ADR-122)"
        ]
    if not _invocations(live, f'merge-base --is-ancestor "${HOST_REVISION}" "${STABLE_REVISION}"'):
        return [
            f"{WORKFLOW_PATH} holds ${HOST_REVISION} over ${STABLE_REVISION} and not "
            "under it, so a release rolled back can migrate a schema under a host "
            "newer than its previous release (ADR-122)"
        ]
    return []


def _pinned_keys(lines: list[str]) -> set[str]:
    """Every name a `--set` argument in `lines` writes before an `=`: a superset of the paths it pins, never fewer.

    Parsing Helm's grammar fails open at each edge it misses; a list member read as a key fails closed.
    """
    text = "\n".join(lines).replace('"', "").replace("'", "")
    first = re.search(r"--set", text)
    # Everything after the first flag, so no continuation, fold, quote or `--` in a value can end the read early.
    after = text[first.end():] if first else ""
    # An indexed member, `push.apps[0]`, pins the list it is a member of.
    return {re.sub(r"\[\d+\]", "", name) for name in re.findall(r"([\w.-]+(?:\[\d+\][\w.-]*)*)=", after)}


def _rollout_holds_shared_values(workloads: dict, workflow: Path = WORKFLOW) -> list[str]:
    """Check 14: where a descriptor names `sharesValues`, the rollout asks for the pairs and compares each one.

    Only while one does: with none, there is nothing to compare.
    """
    if not any(entry.get("sharesValues") for entry in workloads.values()):
        return []
    try:
        text = workflow.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable, so ADR-125's shared values are unchecked: {error}"]
    live = [line for line in text.splitlines() if not line.lstrip().startswith("#")]
    asks = _invocations(live, "canary.py shared-values")
    if not any(_argument("--workload", "WORKLOAD") in run for run in asks):
        return [
            f"{WORKFLOW_PATH} runs no `canary.py shared-values` for the dispatched "
            f"{_argument('--workload', 'WORKLOAD')}, so two releases can roll on values "
            "that disagree (ADR-125)"
        ]
    # A key the rollout sets itself is installed past the candidate values, so comparing them would hold nothing.
    pinned = _pinned_keys(live)
    # A malformed declaration is check 4's refusal; this one reads only what check 4 would admit.
    shared = {
        (name, key)
        for name, entry in workloads.items()
        if isinstance(entry.get("sharesValues"), dict)
        for keys in entry["sharesValues"].values()
        if isinstance(keys, list)
        for key in keys
        if isinstance(key, str) and any(key == p or key.startswith(p + ".") or p.startswith(key + ".") for p in pinned)
    }
    if shared:
        return [
            f"workloads.{name}.sharesValues names {key}, which {WORKFLOW_PATH} sets on the upgrade's own "
            "command line, so the values compared are not the ones installed (ADR-125)"
            for name, key in sorted(shared)
        ]
    compares = _invocations(live, "canary.py values-agree")
    if not any('--values "$RUNNER_TEMP/candidate.json"' in run and "--peer " in run for run in compares):
        return [
            f"{WORKFLOW_PATH} asks `canary.py shared-values` and never compares the "
            "values the release will be installed with to a peer's through `canary.py values-agree`, "
            "so the answer refuses nothing (ADR-125)"
        ]
    return _candidate_values_failures(live, "ADR-125")


def _candidate_values_failures(live: list[str], adr: str) -> list[str]:
    """Checks 14 and 15: `$RUNNER_TEMP/candidate.json` holds the values the upgrade installs.

    Asked by each check that reads the file, so neither leans on the other being active.
    """
    # Overrides alone, as the upgrade applies them: `--all` would carry the old chart's defaults over the new.
    overrides = _invocations(live, "helm get values")
    if not any(
        run.startswith('helm get values "$WORKLOAD" ') and '> "$RUNNER_TEMP/overrides.yaml"' in run
        and "--all" not in run
        for run in overrides
    ):
        return [
            f"{WORKFLOW_PATH} never reads the release's own overrides into "
            '"$RUNNER_TEMP/overrides.yaml" without `--all`, so the values compared are not the ones '
            f"the upgrade installs ({adr})"
        ]
    if not any('cp "deploy/helm/$CHART/values.yaml"' in line for line in live) or not any(
        '-f "$RUNNER_TEMP/overrides.yaml"' in run and '> "$RUNNER_TEMP/candidate.json"' in run
        for run in _invocations(live, "helm template candidate-values")
    ):
        return [
            f"{WORKFLOW_PATH} never coalesces the overrides over the checked-out chart's "
            f'values into "$RUNNER_TEMP/candidate.json", so a changed chart default is never compared ({adr})'
        ]
    return []


def _rollout_keeps_values_empty(workloads: dict, workflow: Path = WORKFLOW) -> list[str]:
    """Check 15: where a descriptor names `emptyUntil`, the rollout asks for the keys and refuses on each one.

    Only while one does: with none, there is nothing to hold empty.
    """
    if not any(entry.get("emptyUntil") for entry in workloads.values()):
        return []
    try:
        text = workflow.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable, so ADR-126's empty values are unchecked: {error}"]
    live = [line for line in text.splitlines() if not line.lstrip().startswith("#")]
    if not any(_argument("--workload", "WORKLOAD") in run for run in _invocations(live, "canary.py empty-until")):
        return [
            f"{WORKFLOW_PATH} runs no `canary.py empty-until` for the dispatched "
            f"{_argument('--workload', 'WORKLOAD')}, so a release can roll naming a value "
            "the other track's workers misread (ADR-126)"
        ]
    # As check 14 argues: a key the rollout sets itself is installed past the values held empty.
    pinned = _pinned_keys(live)
    held = {
        (name, key)
        for name, entry in workloads.items()
        if isinstance(entry.get("emptyUntil"), dict)
        for key in entry["emptyUntil"]
        if any(key == p or key.startswith(p + ".") or p.startswith(key + ".") for p in pinned)
    }
    if held:
        return [
            f"workloads.{name}.emptyUntil names {key}, which {WORKFLOW_PATH} sets on the upgrade's own "
            "command line, so the values held empty are not the ones installed (ADR-126)"
            for name, key in sorted(held)
        ]
    # Each track's revision against the one named: a candidate older than it misreads the stable track's rows.
    for track in ("STABLE_REVISION", "REVISION"):
        if not any(f'git merge-base --is-ancestor "$SINCE" "${track}"' in line for line in live):
            return [
                f'{WORKFLOW_PATH} never asks whether "${track}" carries the revision `canary.py empty-until` '
                "names, so a track older than it is never found (ADR-126)"
            ]
    # Both, on the one line that skips the key: either alone would admit the other track's older revision.
    both = (
        'if git merge-base --is-ancestor "$SINCE" "$STABLE_REVISION" '
        '&& git merge-base --is-ancestor "$SINCE" "$REVISION"; then'
    )
    skips = [
        index for index, line in enumerate(live)
        if line.strip() == both and index + 1 < len(live) and live[index + 1].strip() == "continue"
    ]
    if not skips:
        return [
            f"{WORKFLOW_PATH} skips a key `canary.py empty-until` names without both tracks carrying its "
            "revision, so a track older than it is never refused (ADR-126)"
        ]
    # A stable track carrying the revision may have left rows the values no longer show, so an older tag is refused.
    # Inside the loop and past the skip, where the stable track's test alone means the tag lacks the revision.
    back = 'if git merge-base --is-ancestor "$SINCE" "$STABLE_REVISION"; then'
    ends = [index for index, line in enumerate(live) if line.strip() == 'done < "$RUNNER_TEMP/empty-until.txt"']
    refused = False
    for index, line in enumerate(live):
        if line.strip() != back or not any(skip < index < end for skip in skips for end in ends):
            continue
        # The block's own lines alone. Bash reads no indent, so the scan ends at a line that is `else` or `fi` or opens
        # `elif`, a trailing `;` or comment aside, at any indent, or at a line back at the `if`'s. A nested block's
        # `fi` ends it early, which refuses rather than admits.
        indent, first = len(line) - len(line.lstrip()), None
        for body in live[index + 1:]:
            depth = len(body) - len(body.lstrip())
            word = body.split("#", 1)[0].strip().rstrip(";").strip()
            if depth <= indent or word in ("else", "fi") or word.startswith("elif "):
                break
            first = depth if first is None else first
            refused = refused or (depth == first and body.strip() == "exit 1")
    if not refused:
        return [
            f"{WORKFLOW_PATH} never refuses a tag older than the revision `canary.py empty-until` names while "
            "the stable track carries it, so an older worker inherits the rows it left (ADR-127)"
        ]
    failures = _candidate_values_failures(live, "ADR-126")
    if failures:
        return failures
    # After the candidate values are written, so a step moved above them reads nothing or a stale file.
    written = [index for index, line in enumerate(live) if '> "$RUNNER_TEMP/candidate.json"' in line]
    held_empty = [
        index for index, line in enumerate(live)
        if "canary.py values-empty" in line
        and '--values "$RUNNER_TEMP/candidate.json"' in _invocations(live[index:], "canary.py values-empty")[0]
    ]
    if not written or not any(written[0] < index for index in held_empty):
        return [
            f"{WORKFLOW_PATH} asks `canary.py empty-until` and never holds the values the release will be "
            'installed with, "$RUNNER_TEMP/candidate.json" once written, to empty through '
            "`canary.py values-empty`, so the answer refuses nothing (ADR-126)"
        ]
    return []


def _rollout_reads_the_image_source(workflow: Path = WORKFLOW) -> list[str]:
    """The rollout hands each image's tree to everything that reads `src/`.

    ADR-050 binds a path exported per image to an argument carrying it to
    each reader; a dropped one changes no exit code, so the text is checked.
    """
    try:
        text = workflow.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable, so ADR-050's binding is unchecked: {error}"]

    # Comment lines are dropped first: this file argues at length about the
    # commands it runs, and a check that read those would pass on the prose.
    live = [line for line in text.splitlines() if not line.lstrip().startswith("#")]

    failures = []
    archived = _archived(live)

    for line in live:
        if ASSIGNS_TAG.search(line) and not RESOLVES_TAG.search(line):
            failures.append(
                "a step sets TAG from something other than `canary.py installed-tag`, "
                "so the revision resolved from it is not the running release's (ADR-050)"
            )
    for variable in (IMAGE_SOURCE, INSTALLED_SOURCE):
        # The name is matched whole. `IMAGE_SOURCE=` is also inside
        # `OLD_IMAGE_SOURCE=`, so a consistent rename would satisfy a
        # substring test while exporting a different tree.
        exported = _exported(live, variable)
        if exported is None:
            failures.append(
                f"no step exports {variable} into GITHUB_ENV, so the rollout has "
                "no tree to read it from and ADR-050's binding is not in force"
            )
            continue
        # And it has to be the tree an archive wrote. Exporting the
        # checkout satisfies every name and argument check below while the
        # rollout reads exactly what the binding exists to stop it reading.
        if exported not in archived:
            failures.append(
                f"{variable} is exported as {exported}, which no `git archive` "
                "writes, so the rollout reads a tree nothing took from the "
                "image's revision (ADR-050)"
            )
        else:
            revision, resolved = archived[exported]
            if revision != RESOLVED_REVISION:
                failures.append(
                    f"{variable} is archived from {revision} rather than "
                    f"{RESOLVED_REVISION}, so the tree is not the one the tag "
                    "resolved to (ADR-050)"
                )
            elif not resolved:
                failures.append(
                    f"{variable} is archived from {RESOLVED_REVISION}, which "
                    "the step above it does not set from `canary.py revision`, "
                    "so the name is right and the value is not (ADR-050)"
                )

    # Every reading, because one unbound query is a whole rung judged against
    # the wrong request set, and both tracks are read here.
    readings = _invocations(live, "read_prometheus.py")
    if not readings:
        failures.append(
            f"{WORKFLOW_PATH} runs read_prometheus.py nowhere, so this check's "
            "own subject is missing rather than satisfied"
        )
    for run in readings:
        for flag, variable, track in (
            (SOURCE_FLAG, IMAGE_SOURCE, "the candidate"),
            (INSTALLED_FLAG, INSTALLED_SOURCE, "the stable track"),
        ):
            if _argument(flag, variable) not in run:
                failures.append(
                    f"a read_prometheus.py run takes no {_argument(flag, variable)}, "
                    f"so {track}'s probe exclusion is the checkout's routes and "
                    "not that image's (ADR-050)"
                )

    # One, not every: the `check` job runs the same command against the
    # checkout on a pull request, where there is no image and no tag.
    # Both, because `--source` without the workload it belongs to is a
    # refusal rather than a check, and the workload alone scopes nothing.
    gate = _argument(SOURCE_FLAG, IMAGE_SOURCE)
    runs = _invocations(live, "canary.py check")
    if not any(gate in run for run in runs):
        failures.append(
            f"no `canary.py check` run takes {gate}, so the plan is never held "
            "against the source the deployed image was built from (ADR-050)"
        )
    else:
        # The same run, not merely the same file: a tree without the
        # workload it answers for is refused inside check, and a candidate
        # without the release it replaces leaves the baseline unjudged.
        for flag, variable, cost in (
            ("--workload", "WORKLOAD", "the workload it answers for"),
            (INSTALLED_FLAG, INSTALLED_SOURCE, "the release it replaces"),
        ):
            wanted = _argument(flag, variable)
            if not any(gate in run and wanted in run for run in runs):
                failures.append(
                    f"the `canary.py check` run that takes {gate} takes no "
                    f"{wanted}, so the tree is given without {cost} (ADR-050)"
                )
    return failures


# ADR-153's branch: the condition each side of it carries.
LADDER_ON = "if: steps.ladder.outputs.ladder == 'on'"

LADDER_OFF = "if: steps.ladder.outputs.ladder == 'off'"

STEP_START = re.compile(r"^[ \t]*- (?:name|uses):")


def _steps(live: list[str]) -> list[list[str]]:
    """The workflow's live lines cut at each `- name:` or `- uses:`, so a condition is read with its own run."""
    steps: list[list[str]] = []
    for line in live:
        if STEP_START.match(line) or not steps:
            steps.append([])
        steps[-1].append(line)
    return steps


def _rollout_rolls_off_ladder_in_place(workloads: dict, workflow: Path = WORKFLOW) -> list[str]:
    """Check 16: where a descriptor is `offLadder`, the rollout asks, climbs only `on` and upgrades in place `off`.

    Only while one is: with none, every dispatch climbs and the branch has nothing to send elsewhere.
    """
    if not any("offLadder" in entry for entry in workloads.values()):
        return []
    try:
        text = workflow.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable, so ADR-153's in-place rollout is unchecked: {error}"]
    live = [line for line in text.splitlines() if not line.lstrip().startswith("#")]
    steps = _steps(live)
    workload = _argument("--workload", "WORKLOAD")

    asks = [step for step in steps if any(workload in run for run in _invocations(step, "canary.py ladder"))]
    if not any(any(line.strip() == "id: ladder" for line in step) and
               any('"$GITHUB_OUTPUT"' in line and "ladder=" in line for line in step) for step in asks):
        return [
            f"{WORKFLOW_PATH} has no step `id: ladder` that asks `canary.py ladder` for the dispatched "
            f"{workload} and writes the answer as an output, so nothing tells a release off the ladder "
            "from one on it (ADR-153)"
        ]

    failures = []
    climbing = [step for step in steps
                if _invocations(step, "canary.py required") or _invocations(step, "canary.py plan")]
    if not climbing:
        failures.append(f"{WORKFLOW_PATH} runs no `canary.py required` or `plan`, so this check's subject is missing")
    for step in climbing:
        if not any(line.strip() == LADDER_ON for line in step):
            failures.append(
                f"the {WORKFLOW_PATH} step {step[0].strip()!r} climbs the ladder without `{LADDER_ON}`, so a "
                "release off it is refused there rather than rolled (ADR-153)"
            )
    for run in (run for step in steps for run in _invocations(step, "canary.py required")):
        if workload not in run:
            failures.append(
                f"a `canary.py required` run takes no {workload}, so a step that lost its condition scales a "
                "release off the ladder before anything refuses it (ADR-153)"
            )

    in_place = [step for step in steps if any(line.strip() == LADDER_OFF for line in step)]
    upgrades = [step for step in in_place
                if any(run.startswith('helm upgrade "$WORKLOAD" ') for run in _invocations(step, "helm upgrade"))]
    if not upgrades:
        failures.append(
            f"{WORKFLOW_PATH} has no step under `{LADDER_OFF}` that upgrades the dispatched release, so a "
            "release off the ladder skips the climb and rolls nothing, reported green (ADR-153)"
        )
    for step in in_place:
        if any(_invocations(step, command) for command in ("kubectl scale", "--install", "canary.enabled")):
            failures.append(
                f"the {WORKFLOW_PATH} step {step[0].strip()!r} runs under `{LADDER_OFF}` and scales or installs a "
                "second release, which is the second pod the branch exists to avoid (ADR-153)"
            )
    return failures
