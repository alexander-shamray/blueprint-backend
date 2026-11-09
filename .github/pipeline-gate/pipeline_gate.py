#!/usr/bin/env python3
"""The pipeline asserting things about itself, one subcommand per way §15.1's
staged pipeline can be quietly wrong; the README beside it owns what each
refuses. Stdlib only, on the licence gate's terms.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"
DEPLOY = ROOT / ".github" / "workflows" / "deploy.yml"
SOLUTION = ROOT / "Platform.slnx"

# Floors, set well under each stage's real total on purpose: a floor at the
# total is a ratchet that goes red when somebody legitimately deletes a test
# class, and what this catches is an order-of-magnitude miss, a filter that
# selected nothing or a tenth of what it should. The structural checks in
# `check_stages` catch the small stuff and have no number to age.
STAGE_FLOORS = {
    "architecture": 15,
    "unit": 550,
    "integration": 150,
}


def fail(problems: list[str], subject: str) -> int:
    if not problems:
        return 0
    print(f"pipeline-gate: {len(problems)} problem(s) with {subject}:\n", file=sys.stderr)
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    return 1


# --------------------------------------------------------------------------
# filters
# --------------------------------------------------------------------------

def read_filters(workflow_text: str) -> list[str]:
    """Every path pattern inside the paths-filter step's `filters:` block.

    Text rather than YAML, for the reason `deploy/observability/check.py` gives
    one tree over: there is no stdlib YAML parser, and a gate that needs a
    `pip install` is a gate that gets skipped. The block is a YAML string
    inside a YAML document either way, so a parser would hand back the same
    text to be parsed a second time.
    """
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        match = re.match(r"^(\s*)filters:\s*\|", line)
        if match:
            break
    else:
        return []

    indent = len(match.group(1))
    patterns = []
    for line in lines[index + 1:]:
        if not line.strip():
            continue
        if len(line) - len(line.lstrip()) <= indent and not line.lstrip().startswith("#"):
            break
        found = re.match(r"\s*-\s*'([^']+)'", line)
        if found:
            patterns.append(found.group(1))
    return patterns


def covers(pattern: str, directory: str) -> bool:
    """Whether one filter pattern reaches anything under a directory.

    Prefix matching is §15.1's own model: `src/Services` is spoken for by its
    siblings' filters, which is why the section checks its children
    separately, and a rule demanding `src/Services/**` would fail the correct
    workflow. What this proves is that every deployable is reachable by some
    filter, not that any filter is complete — §15.1's "if changing a file can
    change what a service ships, that file belongs in that service's filter"
    is review's. A negation covers nothing, or an exclusion could satisfy the
    inventory.
    """
    if pattern.startswith("!"):
        return False
    return pattern == f"{directory}/**" or pattern.startswith(f"{directory}/")


def check_filters(root: Path = ROOT) -> list[str]:
    problems: list[str] = []

    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f".github/workflows/ci.yml is not readable: {error}"]

    patterns = read_filters(text)

    # An empty list passes every check below vacuously, so it is the parser
    # that is broken, not the workflow that is complete.
    if not patterns:
        return [
            "found no path patterns under `filters: |` in ci.yml. Every check "
            "here asks whether a directory appears in that list, so an empty "
            "list would pass all of them vacuously — this is the parser "
            "failing, not the inventory being complete"
        ]

    # §15.1: without `some-with-excludes` the default quantifier never
    # evaluates a negated pattern, and the `deploy` filter's exclusion of the
    # compose tree would be decoration.
    if "some-with-excludes" not in text:
        problems.append(
            "the paths-filter step does not set predicate-quantifier: "
            "'some-with-excludes'. The default quantifier never evaluates a "
            "negated pattern, so `!deploy/compose/**` would be silently "
            "ignored and a compose-only change would deploy (§15.1)"
        )

    for parent in ("src", "src/Services"):
        directory = root / parent
        if not directory.is_dir():
            problems.append(f"{parent} is not a directory; the inventory cannot be checked")
            continue

        children = sorted(child.name for child in directory.iterdir() if child.is_dir())
        if not children:
            problems.append(
                f"{parent} has no subdirectories, so its half of this check "
                "would pass vacuously"
            )
            continue

        for child in children:
            path = f"{parent}/{child}"
            if not any(covers(pattern, path) for pattern in patterns):
                problems.append(
                    f"{path} is matched by no filter in ci.yml. §15.1 requires "
                    "every immediate child of src/ and of src/Services/ to "
                    "appear in at least one, because a deployable nothing "
                    "filters is a deployable CI never rebuilds"
                )

    return problems


# --------------------------------------------------------------------------
# images
# --------------------------------------------------------------------------

def read_image_matrix(workflow_text: str) -> list[tuple[str, str]]:
    """The `images` job's matrix, as (filter, dockerfile) pairs.

    Positional rather than block-scoped: the matrix entries are the only place
    in this workflow where `filter:` and `dockerfile:` appear together, and
    pairing them by adjacency is what lets this stay a text match. A `filter:`
    with no `dockerfile:` after it is dropped, which the count check below
    turns into a failure rather than a silence.
    """
    pairs = []
    pending: str | None = None
    for line in workflow_text.splitlines():
        if found := re.match(r"\s*-?\s*filter:\s*(\S+)\s*$", line):
            pending = found.group(1)
        elif found := re.match(r"\s*dockerfile:\s*(\S+)\s*$", line):
            if pending is not None:
                pairs.append((pending, found.group(1)))
                pending = None
    return pairs


def check_images(root: Path = ROOT) -> list[str]:
    """Every image is built, and everything built is an image.

    Both directions, and checks neither can make: an undefined `filter`, for one,
    makes `needs.changes.outputs[matrix.filter]` empty, so only the guard step notices.
    """
    problems: list[str] = []

    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f".github/workflows/ci.yml is not readable: {error}"]

    matrix = read_image_matrix(text)
    if not matrix:
        return [
            "found no (filter, dockerfile) pairs in the images matrix in "
            "ci.yml. Every check here compares against that list, so an empty "
            "one passes them all vacuously — the parser is what is broken"
        ]

    declared = {dockerfile for _filter, dockerfile in matrix}

    # Forward: every application Dockerfile is built by some entry.
    on_disk = {
        str(path.relative_to(root)).replace("\\", "/")
        for path in (root / "src").rglob("Dockerfile")
    }
    if not on_disk:
        problems.append(
            "found no Dockerfile under src/: the forward check would pass "
            "vacuously, so the search is what is broken"
        )
    for dockerfile in sorted(on_disk - declared):
        problems.append(
            f"{dockerfile} is built by no entry in ci.yml's images matrix. "
            "§15.1 builds every image a changed service ships, and one nothing names "
            "is one CI never builds — which surfaces on the next compose pull "
            "request rather than on the change that broke it"
        )

    # Reverse: every entry names a Dockerfile that is there.
    for dockerfile in sorted(declared - on_disk):
        problems.append(
            f"ci.yml's images matrix names {dockerfile}, which does not exist. "
            "The step is path-filtered, so this fails on the day it is next "
            "selected and not on the change that renamed it"
        )

    # And the filter each entry reads back has to be one that exists...
    by_filter = read_filters_by_name(text)
    if not by_filter:
        problems.append(
            "found no filter names in the paths-filter block: the two checks "
            "below would pass vacuously"
        )
    for name in sorted({filter_name for filter_name, _ in matrix} - set(by_filter)):
        problems.append(
            f"ci.yml's images matrix reads the filter {name!r}, which the "
            "paths-filter step does not define. A GitHub expression indexing a "
            "missing output is the empty string, so that entry's build "
            "never runs and its guard step fails"
        )

    # ...and the `changes` job has to export it, which defining it does not do:
    # `needs.changes.outputs.gateway` reads a job output, and deleting the one
    # `outputs:` line leaves the filter defined and the checks above green.
    exported = read_job_outputs(text, "changes")
    if not exported:
        problems.append(
            "found no outputs on the changes job: the check below would pass "
            "vacuously"
        )
    for name in sorted({filter_name for filter_name, _ in matrix} - exported):
        if name in by_filter:
            problems.append(
                f"ci.yml's changes job defines the filter {name!r} and does not "
                "export it as a job output, so `needs.changes.outputs."
                f"{name}` is empty and every leg under that filter fails its "
                "guard step"
            )

    # ...and the job's `if:` is the split from publish-images and nothing else.
    # GitHub expands a matrix only after the job's condition passes, so a skipped
    # job is one check whose name shows `${{ matrix.image }}` verbatim: this one
    # is skipped only on main, and each step tests its own filter.
    condition = read_job_condition(text, "images")
    if condition is None:
        problems.append(
            "found no `images:` job in ci.yml: the check below would pass "
            "vacuously, so the parser or the job's key is what is broken"
        )
    elif not condition:
        problems.append(
            f"the images job carries no job-level `if: {IMAGES_CONDITION}`, so it "
            f"builds every image twice on main, beside {PUBLISH_JOB}"
        )
    elif condition != IMAGES_CONDITION:
        problems.append(
            f"the images job carries a job-level `if: {condition}`. A matrix "
            "job that condition skips is never expanded, so the check is named "
            "with its expression unevaluated; leave the decision to each "
            "step's `if:` on needs.changes.outputs[matrix.filter]"
        )

    problems.extend(_check_publish_job(text))

    # ...and it has to be the right one. An entry pairing the gateway's
    # Dockerfile with `filter: catalog` passes both directions of the inventory
    # and the name check, and rebuilds the wrong image.
    for filter_name, dockerfile in sorted(matrix):
        patterns = by_filter.get(filter_name)
        if patterns is None:
            continue
        if not any(_matches(pattern, dockerfile) for pattern in patterns):
            problems.append(
                f"ci.yml's images matrix builds {dockerfile} under the filter "
                f"{filter_name!r}, whose patterns do not match that path. The "
                "entry is well-formed and wired to the wrong service: a change "
                "to it rebuilds the wrong image, and the right one is never "
                "built at all"
            )

    problems.extend(check_sboms(text))
    problems.extend(check_retries(text))
    try:
        deploy_text = DEPLOY.read_text(encoding="utf-8")
    except OSError as error:
        problems.append(f".github/workflows/deploy.yml is not readable: {error}")
    else:
        problems.extend(check_supply_chain(text, deploy_text))
    return problems


SBOM_ACTION = "anchore/sbom-action@"
IMAGES_CONDITION = "${{ github.ref != 'refs/heads/main' }}"
PUBLISH_JOB = "publish-images"
PUBLISH_CONDITION = (
    "${{ contains(fromJSON('[\"push\",\"workflow_dispatch\"]'), github.event_name) "
    "&& github.ref == 'refs/heads/main' }}"
)
# Both image jobs take one matrix, so neither inventory can drift from the other.
IMAGE_LEGS_ALIAS = re.compile(r"^ {4}strategy: \*image-legs\s*$", flags=re.MULTILINE)


def _check_publish_job(workflow_text: str) -> list[str]:
    """publish-images holds what signs and pushes, so a pull request never reaches it (ADR-197)."""
    condition = read_job_condition(workflow_text, PUBLISH_JOB)
    if condition is None:
        return [
            f"found no `{PUBLISH_JOB}:` job in ci.yml: the checks on it would pass "
            "vacuously, so the parser or the job's key is what is broken"
        ]

    problems: list[str] = []
    if condition != PUBLISH_CONDITION:
        problems.append(
            f"the {PUBLISH_JOB} job's own condition is {condition!r}, not {PUBLISH_CONDITION!r}, so it "
            "is not kept to a push or dispatch on main. Its grants are held from the job's first step, whatever "
            "each step's own `if:` says, so a pull request's Dockerfiles build beside them (ADR-197)"
        )

    jobs = read_jobs(workflow_text)
    if not IMAGE_LEGS_ALIAS.search("\n".join(jobs.get(PUBLISH_JOB, []))):
        problems.append(
            f"the {PUBLISH_JOB} job does not take the images job's matrix as `strategy: *image-legs`, "
            "so a second list decides what main builds, and the inventory reads pairs from either"
        )

    builds = {
        job: [step["run"] for step in read_job_steps(workflow_text, job) if DOCKER_BUILD.search(step.get("run", ""))]
        for job in ("images", PUBLISH_JOB)
    }
    if builds["images"] and builds[PUBLISH_JOB] and builds["images"] != builds[PUBLISH_JOB]:
        problems.append(
            f"the {PUBLISH_JOB} job's `docker build` is not the line the images job runs, so a "
            "green pull request says nothing about the build main signs"
        )
    return problems


def read_job_steps(workflow_text: str, job: str) -> list[dict[str, str]]:
    """One job's steps, in order, as {key: value} over the keys a step holds.

    Its own keys and `with:`'s are both kept, so `image` is the SBOM step's
    input, and a value runs on over deeper lines to read a folded `run: >`.
    """
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        if re.match(rf"^\s{{2}}{re.escape(job)}:\s*$", line):
            break
    else:
        return []

    steps: list[dict[str, str]] = []
    inside = False
    key: str | None = None
    for line in lines[index + 1:]:
        if re.match(r"^\s{2}\S", line):          # the next job
            break
        if re.match(r"^\s{4}steps:\s*$", line):
            inside = True
            continue
        if not inside or not line.strip() or line.lstrip().startswith("#"):
            continue
        if re.match(r"^\s{4}\S", line):
            inside = False
            continue
        if found := re.match(r"^\s{6}-\s+([a-z-]+):\s*(.*)$", line):
            steps.append({})
            key = found.group(1)
            steps[-1][key] = found.group(2)
        elif steps and (found := re.match(r"^(?:\s{8}|\s{10})([a-z-]+):\s*(.*)$", line)):
            key = found.group(1)
            steps[-1][key] = found.group(2)
        elif steps and key is not None:
            steps[-1][key] += " " + line.strip()
    return steps


def check_sboms(workflow_text: str) -> list[str]:
    """Every image either image job builds has an SBOM made from it (ADR-071).

    Per build step, because the inventory above holds the matrix to the
    Dockerfiles, so one read of the reference a build tags covers every leg.
    """
    problems: list[str] = []
    for job in ("images", PUBLISH_JOB):
        steps = read_job_steps(workflow_text, job)
        builds = [(index, step) for index, step in enumerate(steps) if DOCKER_BUILD.search(step.get("run", ""))]
        if not builds:
            problems.append(
                f"found no `docker build` step in the {job} job: the SBOM check "
                "would pass vacuously, so the parser or the job is what is broken"
            )
            continue

        for index, build in builds:
            tag = re.search(r"--tag\s+((?:\$\{\{[^}]*\}\}|[^\s$])+)", build["run"])
            if tag is None:
                problems.append(
                    f"a {job} job build step names no --tag, so no SBOM step can be "
                    "shown to read the image it builds"
                )
                continue
            if not any(
                re.fullmatch(rf"{re.escape(SBOM_ACTION)}[0-9a-f]{{40}}(?:\s+#.*)?", step.get("uses", "").strip())
                and step.get("image", "").strip() == tag.group(1)
                and step.get("if", "").strip() == build.get("if", "").strip()
                for step in steps[index + 1:]
            ):
                problems.append(
                    f"the {job} job builds {tag.group(1)} and no {SBOM_ACTION} step "
                    "pinned to a commit reads that reference after it under the same "
                    "`if:`. ADR-071 gives every image the pipeline builds an SBOM, and "
                    "a leg without one ships an image nothing has inventoried"
                )
    return problems


RETRY_HELPER = ".github/pipeline-gate/image_retry.sh"
NOTE_ARTIFACT_ACTION = "actions/upload-artifact@"
RETRY_RECORD = "image-retries.txt"
# The keys that say when an attempt runs; every other key says what it does.
ATTEMPT_KEYS = {"name", "id", "if", "continue-on-error"}


def _what_it_does(step: dict[str, str]) -> dict[str, str]:
    return {key: value.strip() for key, value in step.items() if key not in ATTEMPT_KEYS}


def _keeps_the_retry_record(step: dict[str, str]) -> bool:
    return step.get("uses", "").strip().startswith(NOTE_ARTIFACT_ACTION) and RETRY_RECORD in step.get("path", "")


def check_retries(workflow_text: str) -> list[str]:
    """A step of an image job may fail only with a noted second attempt behind it.

    `continue-on-error` alone lets the main tag move onto an image with no
    signature or attestation, and nothing after the step would say so.
    """
    problems: list[str] = []
    for job in ("images", PUBLISH_JOB):
        steps = read_job_steps(workflow_text, job)
        for index, step in enumerate(steps):
            if "continue-on-error" not in step or _keeps_the_retry_record(step):
                continue
            command = step.get("uses", step.get("run", "")).strip()[:60]
            subject = f"the {job} job's step {index + 1} may fail ({command!r})"
            identifier = step.get("id", "").strip()
            if not identifier:
                problems.append(f"{subject} and has no `id`, so no second attempt can read its outcome")
                continue

            guard = f"${{{{ steps.{identifier}.outcome == 'failure' }}}}"
            note, second = (steps[index + 1:index + 3] + [{}, {}])[:2]
            if note.get("if", "").strip() != guard or f"{RETRY_HELPER} note" not in note.get("run", ""):
                problems.append(
                    f"{subject} and no note under `if: {guard}` follows it. Either its failure passes "
                    "with no second attempt, or a retry hides the failure it recovered from"
                )
                continue
            if second.get("if", "").strip() != guard or "continue-on-error" in second:
                problems.append(
                    f"{subject} and the step after its note is no second attempt under `if: {guard}` "
                    "that fails the job when it fails too, so the first attempt's failure can pass"
                )
                continue
            if _what_it_does(second) != _what_it_does(step):
                problems.append(
                    f"{subject} and its second attempt is not what the first attempt does, so what "
                    "the image carries would depend on which attempt ran"
                )
    return problems


# What a step gated on main-push says, in the pieces the gate looks for: the
# leg's own filter, the ref, and an event that is not a pull request.
FILTER_TERM = "needs.changes.outputs[matrix.filter] == 'true'"
MAIN_REF = "github.ref == 'refs/heads/main'"
LOGIN_ACTION = "docker/login-action@"
COSIGN_ACTION = "sigstore/cosign-installer@"
PROVENANCE_ACTION = "actions/attest-build-provenance@"
SBOM_ATTESTATION_ACTION = "actions/attest-sbom@"
MOVING_TAG_PUSH = r'\bdocker push\s+"\$REGISTRY/[^"]*:main"'
# `docker buildx imagetools inspect` reads a tag and builds nothing.
DOCKER_BUILD = re.compile(r"\bdocker build\b")
MOVING_TAG_GUARD = r"\bgit merge-base --is-ancestor\b"
REVISION_LABEL = "org.opencontainers.image.revision="
CI_IDENTITY = "/.github/workflows/ci.yml@refs/heads/main"
HELM_COMMAND = re.compile(r"(?:^|[\s;&|(`])helm\s+[a-z]")
HELM_UPGRADE = re.compile(r"(?:^|[\s;&|(`])helm\s+upgrade\b")


def _on_main_push(step: dict[str, str]) -> bool:
    condition = step.get("if", "")
    return (
        FILTER_TERM in condition
        and MAIN_REF in condition
        and re.search(r"[\"']push[\"']", condition) is not None
        and "pull_request" not in condition
    )


def _located(steps: list[dict[str, str]], window: range, *, uses: str | None = None,
             run: str | None = None) -> int | None:
    for index in window:
        step = steps[index]
        if uses is not None and step.get("uses", "").strip().startswith(uses):
            return index
        if run is not None and re.search(run, step.get("run", "")):
            return index
    return None


def _reference(run: str, command: str) -> str:
    """The quoted image reference a command names, or its first word if unquoted."""
    found = re.search(rf'{command}\b[^"\n]*"([^"]*)"', run) or re.search(rf"{command}\b(?:\s+-\S+)*\s+(\S+)", run)
    return found.group(1) if found else ""


def read_workflow_env(workflow_text: str, key: str) -> str | None:
    """One value of the workflow-level `env:` block, or None."""
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        if not re.match(r"^env:\s*$", line):
            continue
        for follow in lines[index + 1:]:
            if not follow.strip() or follow.lstrip().startswith("#"):
                continue
            if not follow.startswith("  "):
                break
            if found := re.match(rf"^  {re.escape(key)}:\s*(.*?)\s*$", follow):
                return found.group(1)
        return None
    return None


def read_helm_upgrades(workflow_text: str) -> list[str]:
    """Every `helm upgrade` command a workflow runs, continuation lines joined."""
    lines = workflow_text.splitlines()
    commands = []
    index = 0
    while index < len(lines):
        line = lines[index]
        if not line.lstrip().startswith("#") and HELM_UPGRADE.search(line):
            command = line.strip()
            while command.endswith("\\") and index + 1 < len(lines):
                index += 1
                command = command[:-1] + " " + lines[index].strip()
            commands.append(command)
        index += 1
    return commands


def check_supply_chain(workflow_text: str, deploy_text: str) -> list[str]:
    """Every build in the `publish-images` job is pushed, signed and attested on
    main by digest, and the rollout verifies it before Helm runs (ADR-109).

    Coverage rather than findings: a job that stops signing is red.
    """
    steps = read_job_steps(workflow_text, PUBLISH_JOB)
    builds = [(index, step) for index, step in enumerate(steps) if DOCKER_BUILD.search(step.get("run", ""))]
    if not builds:
        return [
            f"found no `docker build` step in the {PUBLISH_JOB} job: the supply-chain check "
            "would pass vacuously, so the parser or the job is what is broken"
        ]

    problems: list[str] = []
    for position, (index, build) in enumerate(builds):
        end = builds[position + 1][0] if position + 1 < len(builds) else len(steps)
        window = range(index + 1, end)
        subject = f"the {PUBLISH_JOB} job's build at step {index + 1}"
        found = {
            "registry sign-in": _located(steps, window, uses=LOGIN_ACTION),
            "`docker push`": _located(steps, window, run=r"\bdocker push\b"),
            "cosign installer": _located(steps, window, uses=COSIGN_ACTION),
            "`cosign sign`": _located(steps, window, run=r"\bcosign sign\b"),
            "provenance attestation": _located(steps, window, uses=PROVENANCE_ACTION),
            "SBOM attestation": _located(steps, window, uses=SBOM_ATTESTATION_ACTION),
            "push of the main tag": _located(steps, window, run=MOVING_TAG_PUSH),
        }
        missing = [name for name, where in found.items() if where is None]
        if missing:
            problems.append(
                f"{subject} has no {', no '.join(missing)} after it. ADR-109 has every image "
                "the job builds pushed, signed and attested on main, and a leg without "
                "one ships an image the rollout then refuses"
            )
            continue

        login, push, installer, sign, provenance, attestation, moved = found.values()
        for name, where in found.items():
            if not _on_main_push(steps[where]):
                problems.append(
                    f"{subject}: the {name} does not run under the main-push condition "
                    f"({FILTER_TERM}, {MAIN_REF} and a push or dispatch event). Without it "
                    "a pull request pushes, or a leg that built nothing signs nothing"
                )
        for earlier, later, why in (
            (login, push, "the push is refused without a login"),
            (push, sign, "there is no digest to sign before the push"),
            (installer, sign, "cosign is not on the runner before its installer"),
            (push, provenance, "there is no digest to attest before the push"),
            (push, attestation, "there is no digest to attest before the push"),
            (sign, moved, "the main tag names an image before its signature exists"),
            (provenance, moved, "the main tag names an image before its provenance exists"),
            (attestation, moved, "the main tag names an image before its SBOM attestation exists"),
        ):
            if earlier > later:
                problems.append(f"{subject}: a step runs before the one it needs, because {why}")

        if REVISION_LABEL not in build["run"]:
            problems.append(
                f"{subject} labels no `{REVISION_LABEL}`: the main tag's move reads the commit "
                "the tag names from it, so without it nothing says which build is older (#1112)"
            )
        commands = chr(10).join(
            line for line in steps[moved]["run"].splitlines() if not line.lstrip().startswith("#")
        )
        guard = re.search(MOVING_TAG_GUARD, commands)
        move = re.search(MOVING_TAG_PUSH, commands)
        if not guard or (move and guard.start() > move.start()):
            problems.append(
                f"{subject}: the step that moves the main tag does not ask, before it pushes, "
                "whether the commit the tag names is an ancestor of this build's, so a build that finishes late "
                "moves the tag back (#1112, ADR-134)"
            )

        pushed = _reference(steps[push]["run"], "docker push")
        tagged = f"--tag {pushed}" in build["run"] or f'--tag "{pushed}"' in build["run"]
        if not pushed.startswith("$REGISTRY/") or not tagged:
            problems.append(
                f"{subject} tags no `$REGISTRY/...` reference that the push then pushes "
                f"(the push names {pushed!r}), so the registry address is spelled outside "
                "REGISTRY or the push sends an image the build never tagged"
            )

        identifier = steps[push].get("id", "").strip()
        digest = f"steps.{identifier}.outputs.digest"
        if not identifier:
            problems.append(f"{subject}: the push step has no `id`, so no later step can read its digest")
            continue

        signed = _reference(steps[sign]["run"], "cosign sign")
        _, at, after = signed.partition("@")
        variable = re.fullmatch(r"\$\{?(\w+)\}?", after)
        from_env = variable is not None and re.search(
            rf"\b{variable.group(1)}:\s*\$\{{\{{\s*{re.escape(digest)}\s*\}}\}}", steps[sign].get("env", "")
        )
        if not at or not (digest in after or from_env):
            problems.append(
                f"{subject}: `cosign sign` names {signed!r}, not the pushed digest "
                f"({digest}). A tag can be pushed over, so a signature made by tag can land on other bytes"
            )

        for name, where in (("provenance", provenance), ("SBOM", attestation)):
            attest = steps[where]
            if digest not in attest.get("subject-digest", ""):
                problems.append(f"{subject}: the {name} attestation's subject-digest is not the pushed digest ({digest})")
            if attest.get("push-to-registry", "").strip() != "true":
                problems.append(
                    f"{subject}: the {name} attestation is not pushed to the registry, "
                    "so nothing beside the workflow run can find it"
                )
            if not attest.get("subject-name", "").strip().startswith("${{ env.REGISTRY }}/"):
                problems.append(f"{subject}: the {name} attestation's subject-name is not under REGISTRY")

        sbom = _located(steps, window, uses=SBOM_ACTION)
        if sbom is not None and steps[attestation].get("sbom-path", "").strip() != steps[sbom].get("output-file", "<none>").strip():
            problems.append(
                f"{subject}: the SBOM attestation reads {steps[attestation].get('sbom-path')!r}, "
                "not the file the SBOM step wrote, so the attested inventory is not the artefact's"
            )

    problems.extend(_check_rollout(workflow_text, deploy_text))
    return problems


def _check_rollout(workflow_text: str, deploy_text: str) -> list[str]:
    """deploy.yml verifies before it touches Helm, pins what it verified, and shares REGISTRY."""
    problems: list[str] = []

    registry = read_workflow_env(workflow_text, "REGISTRY")
    deployed = read_workflow_env(deploy_text, "REGISTRY")
    if registry is None:
        problems.append("ci.yml declares no workflow-level REGISTRY, the one owner of the registry address")
    elif registry != deployed:
        problems.append(
            f"ci.yml's REGISTRY is {registry!r} and deploy.yml's is {deployed!r}. The rollout would "
            "verify an address CI never pushed to"
        )
    for name, text in (("ci.yml", workflow_text), ("deploy.yml", deploy_text)):
        for line in text.splitlines():
            if line.lstrip().startswith("#") or not re.search(r"ghcr\.io/", line):
                continue
            if re.match(r"^  REGISTRY:", line):
                continue
            problems.append(
                f"{name} spells a registry address outside REGISTRY: {line.strip()!r}. "
                "The address has one owner, so moving registries stays one value"
            )

    rollout = read_job_steps(deploy_text, "rollout")
    if not rollout:
        return problems + ["found no `rollout` job in deploy.yml: the admission check would pass vacuously"]

    verify = _located(rollout, range(len(rollout)), run=r"\bcosign verify\b")
    helm = _located(rollout, range(len(rollout)), run=HELM_COMMAND.pattern)
    if helm is None:
        problems.append(
            "found no step in deploy.yml's rollout that runs helm: the order check would pass "
            "vacuously, so the parser or the job is what is broken"
        )
    if verify is None:
        problems.append(
            "deploy.yml's rollout has no `cosign verify` step: ADR-071's admission check is not made, "
            "and any image in the registry rolls"
        )
    else:
        text = " ".join(rollout[verify].values())
        for needle, why in (
            (CI_IDENTITY, "pinned to ci.yml on main"),
            ("--certificate-identity", "given an identity"),
            ("--certificate-oidc-issuer", "given an issuer"),
            ("verified-digest", "reading the digest it verified"),
            ("--source-ref refs/heads/main", "pinning the provenance to main"),
        ):
            if needle not in text:
                problems.append(f"deploy.yml's `cosign verify` step is not {why} ({needle!r} is missing)")
        if helm is not None and verify > helm:
            problems.append(
                "deploy.yml's `cosign verify` runs after the first step that runs helm, so the "
                "release is read, or changed, before the image is admitted"
            )

    upgrades = read_helm_upgrades(deploy_text)
    if not upgrades:
        problems.append("found no `helm upgrade` in deploy.yml: the digest check would pass vacuously")
    for command in upgrades:
        for value in ("image.digest", "image.migratorDigest"):
            if not re.search(rf"--set-string\s+{re.escape(value)}=", command):
                problems.append(
                    f"a `helm upgrade` in deploy.yml does not pass --set-string {value}, so it "
                    f"pulls by tag what the rollout verified by digest: {command[:60]!r}"
                )
    return problems


# --------------------------------------------------------------------------
# actions
# --------------------------------------------------------------------------

# GitHub publishes these, so whether they ride a major tag is a separate
# decision; every other owner's action is pinned to a commit.
FIRST_PARTY_OWNERS = {"actions"}


def read_uses(workflow_text: str) -> list[str]:
    """Every `uses:` reference in one workflow, a step's or a job's."""
    return re.findall(r"^\s*(?:-\s+)?uses:\s*['\"]?([^\s'\"#]+)", workflow_text, flags=re.MULTILINE)


def check_actions(root: Path = ROOT) -> list[str]:
    """Every third-party action a workflow runs is pinned to a commit (§15.1).

    A tag can be moved to other code, and `paths-filter` decides which gates
    run, so a moved tag is a way to switch them off that no diff shows.
    """
    workflows = sorted(
        path for path in (root / ".github" / "workflows").glob("*")
        if path.suffix in (".yml", ".yaml")
    )
    references = [
        (path.name, reference)
        for path in workflows
        for reference in read_uses(path.read_text(encoding="utf-8"))
    ]
    if not references:
        return [
            "found no `uses:` in .github/workflows: the pin check would pass "
            "vacuously, so the parser or the directory is what is broken"
        ]

    problems: list[str] = []
    for name, reference in references:
        if reference.startswith("./"):
            continue
        if reference.startswith("docker://"):
            if "@sha256:" not in reference:
                problems.append(f"{name} runs {reference}, a container image not pinned to a digest")
            continue
        action, _, ref = reference.partition("@")
        if action.split("/")[0] in FIRST_PARTY_OWNERS:
            continue
        if not re.fullmatch(r"[0-9a-f]{40}", ref):
            problems.append(
                f"{name} runs {reference}, a third-party action not pinned to a "
                "commit. A tag can be moved to other code; pin the full SHA and "
                "keep the version as a comment"
            )
    return problems


# --------------------------------------------------------------------------
# grants
# --------------------------------------------------------------------------

CHECKOUT = re.compile(r"^\s*(?:-\s+)?uses:\s*['\"]?actions/checkout@", flags=re.MULTILINE)
PULL_REQUEST_TRIGGER = re.compile(
    r"^on:.*\bpull_request|^  (?:-\s+)?['\"]?pull_request(?:_target)?\b", flags=re.MULTILINE)


def read_jobs(workflow_text: str) -> dict[str, list[str]]:
    """Each job's own lines under `jobs:`, by its key."""
    jobs: dict[str, list[str]] = {}
    current: list[str] | None = None
    inside = False
    for line in workflow_text.splitlines():
        if re.match(r"^jobs:\s*$", line):
            inside = True
        elif not inside:
            continue
        elif re.match(r"^[^\s#]", line):
            break
        elif found := re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line):
            current = jobs.setdefault(found.group(1), [])
        elif current is not None:
            current.append(line)
    return jobs


def read_grants(lines: list[str], indent: int) -> list[str] | None:
    """The write grants of the `permissions:` at one indentation, or None for no block."""
    found = _key(lines, "permissions", indent)
    if found is None:
        return None
    value, nested = found
    if value.strip().strip("'\"") == "write-all":
        return ["write-all"]
    # The inline value as well as the nested lines, for `permissions: { packages: write }`.
    return [
        f"{name}: write"
        for text in (value, *nested)
        if not text.lstrip().startswith("#")
        for name in re.findall(r"([a-z-]+):\s*['\"]?write\b", text)
    ]


def _split_outside_parentheses(expression: str, operator: str) -> list[str]:
    """One expression's terms at the top level, quotes and parentheses kept whole."""
    terms: list[str] = []
    start = depth = index = 0
    quoted = False
    while index < len(expression):
        character = expression[index]
        if character == "'":
            quoted = not quoted
        elif not quoted and character == "(":
            depth += 1
        elif not quoted and character == ")":
            depth -= 1
        elif not quoted and depth == 0 and expression.startswith(operator, index):
            terms.append(expression[start:index])
            index += len(operator)
            start = index
            continue
        index += 1
    terms.append(expression[start:])
    return [term.strip() for term in terms]


def _first_parenthesis_closes_last(term: str) -> bool:
    """Whether the `(` a term opens with is closed by its final character and no earlier one."""
    depth = 0
    quoted = False
    for index, character in enumerate(term):
        if character == "'":
            quoted = not quoted
        elif not quoted and character == "(":
            depth += 1
        elif not quoted and character == ")":
            depth -= 1
            if depth == 0:
                return index == len(term) - 1
    return False


def _unwrapped(term: str) -> str:
    """A term without the parentheses that enclose the whole of it."""
    while term.startswith("(") and _first_parenthesis_closes_last(term):
        term = term[1:-1].strip()
    return term


def _term_names_an_event_or_main(term: str, ref_decides: bool) -> bool:
    """One test that is false on a pull request's run, whatever else is true."""
    if "pull_request" in term:
        return False
    return (
        (ref_decides and term == MAIN_REF)
        or re.fullmatch(r"github\.event_name == '[a-z_]+'", term) is not None
        or re.fullmatch(r"contains\(fromJSON\('\[[^]]*\]'\), github\.event_name\)", term) is not None
    )


def _keeps_pull_requests_out(condition: str, ref_decides: bool = True) -> bool:
    """Whether a job's own condition is false on every pull request's run.

    `&&` binds tighter than `||`. A condition this cannot read is refused, and a
    `pull_request_target` run's ref is the base branch, so there only an event test counts.
    """
    expression = re.sub(r"^[>|][-+]?\s*", "", condition.strip())
    expression = re.sub(r"^\$\{\{\s*(.*?)\s*\}\}$", r"\1", expression)
    return _false_on_a_pull_request(expression, ref_decides)


def _false_on_a_pull_request(expression: str, ref_decides: bool) -> bool:
    expression = _unwrapped(expression)
    sides = _split_outside_parentheses(expression, "||")
    if len(sides) > 1:
        return all(_false_on_a_pull_request(side, ref_decides) for side in sides)
    terms = _split_outside_parentheses(expression, "&&")
    if len(terms) > 1:
        return any(_false_on_a_pull_request(term, ref_decides) for term in terms)
    return _term_names_an_event_or_main(expression, ref_decides)


def check_grants(root: Path = ROOT) -> list[str]:
    """No job a pull request reaches holds a write grant beside the branch's code (ADR-197).

    The checkout is what brings the branch in: a job with none runs only the
    workflow's own text. It holds the shape on what lands, not on a branch's edit.
    """
    workflows = sorted(
        path for path in (root / ".github" / "workflows").glob("*")
        if path.suffix in (".yml", ".yaml")
    )
    if not workflows:
        return ["found no workflow under .github/workflows: the grant check would pass vacuously"]

    held = 0
    problems: list[str] = []
    for path in workflows:
        text = path.read_text(encoding="utf-8")
        inherited = read_grants(text.splitlines(), 0) or []
        for job, lines in read_jobs(text).items():
            own = read_grants(lines, 4)
            grants = inherited if own is None else own
            if not grants:
                continue
            held += 1
            if not PULL_REQUEST_TRIGGER.search(text) or not CHECKOUT.search("\n".join(lines)):
                continue
            condition = read_job_condition(text, job) or ""
            if not _keeps_pull_requests_out(condition, ref_decides="pull_request_target" not in text):
                problems.append(
                    f"{path.name}'s job `{job}` holds {', '.join(grants)} and checks out the branch, and "
                    "its own `if:` does not keep a pull request out, so whatever that branch runs in the "
                    "job holds the grant. Move what spends the grant to a job that runs only on main, or "
                    "to one with no checkout (ADR-197)"
                )
    if not held:
        return [
            "found no job holding a write grant under .github/workflows: the grant check would "
            "pass vacuously, so the parser or the directory is what is broken"
        ]
    return problems


# --------------------------------------------------------------------------
# alerts
# --------------------------------------------------------------------------

ALERT_WORKFLOW = "main-red.yml"
ALERT_CONCLUSIONS = ("failure", "timed_out", "startup_failure")


def _key(lines: list[str], key: str, indent: int) -> tuple[str, list[str]] | None:
    """A key at one indentation: its inline value and the lines nested under it."""
    pattern = re.compile(rf"^ {{{indent}}}{re.escape(key)}:(.*)$")
    for index, line in enumerate(lines):
        match = pattern.match(line)
        if not match:
            continue
        nested: list[str] = []
        for following in lines[index + 1:]:
            stripped = following.strip()
            if stripped and not stripped.startswith("#") and len(following) - len(following.lstrip()) <= indent:
                break
            nested.append(following)
        return match.group(1).split(" #")[0].strip(), nested
    return None


def _list(value: str, nested: list[str]) -> list[str]:
    """A YAML list in its flow form, `[a, b]`, its block form, `- a`, or a scalar."""
    if value.startswith("["):
        items = value.strip("[]").split(",")
    elif value:
        items = [value]
    else:
        items = [line.strip()[2:].split(" #")[0] for line in nested if line.strip().startswith("- ")]
    return [item.strip().strip("'\"") for item in items if item.strip()]


def read_workflow_name(workflow_text: str) -> str | None:
    match = re.search(r"^name:\s*['\"]?(.+?)['\"]?\s*$", workflow_text, flags=re.MULTILINE)
    return match.group(1) if match else None


def runs_on_main_push(workflow_text: str) -> bool:
    """Whether a push to `main` starts this workflow."""
    on = _key(workflow_text.splitlines(), "on", 0)
    if on is None:
        return False
    value, nested = on
    if value:
        return "push" in _list(value, [])
    push = _key(nested, "push", 2)
    if push is None:
        return False
    filters = push[1]
    if (branches := _key(filters, "branches", 4)) is not None:
        return "main" in _list(*branches)
    if (ignored := _key(filters, "branches-ignore", 4)) is not None:
        return "main" not in _list(*ignored)
    # A push filtered to tags alone starts no run for a branch.
    return _key(filters, "tags", 4) is None


def read_watched(alert_text: str) -> list[str]:
    """The workflow names the alert's `workflow_run` trigger lists."""
    on = _key(alert_text.splitlines(), "on", 0)
    run = _key(on[1], "workflow_run", 2) if on else None
    workflows = _key(run[1], "workflows", 4) if run else None
    return _list(*workflows) if workflows else []


def check_alert_conclusions(alert_text: str) -> list[str]:
    """The alert still hears every conclusion a red run ends on (ADR-155, ADR-196)."""
    filing = read_job_condition(alert_text, "file") or ""

    # Read list by list: the run's conclusion and the need's result share a word.
    def listed_beside(subject: str) -> str:
        found = re.search(rf"fromJSON\('\[([^']*)\]'\),\s*{re.escape(subject)}", filing)
        return found.group(1) if found else ""

    conclusions = listed_beside("github.event.workflow_run.conclusion")
    problems = [
        f"{ALERT_WORKFLOW}'s `file` job does not file on `{conclusion}`, so a run ending so alerts nobody"
        for conclusion in ALERT_CONCLUSIONS
        if f'"{conclusion}"' not in conclusions
    ]
    results = listed_beside("needs.read-cancelled.result")
    if ("== 'cancelled'" not in (read_job_condition(alert_text, "read-cancelled") or "")
            or "always()" not in filing
            or "needs.read-cancelled.outputs.reason" not in filing
            or '"failure"' not in results
            or '"cancelled"' not in results):
        problems.append(
            f"{ALERT_WORKFLOW} does not read a `cancelled` run through `read-cancelled` into its `file` job, "
            "so a job past its time limit alerts nobody"
        )
    return problems


def check_alerts(root: Path = ROOT) -> list[str]:
    """Every workflow a push to `main` runs is one the red-main alert watches (ADR-155).

    `workflow_run` matches a workflow by its name, and a listed name nothing
    carries matches nothing without an error, so the list is held both ways.
    """
    directory = root / ".github" / "workflows"
    alert = directory / ALERT_WORKFLOW
    if not alert.is_file():
        return [f"found no {ALERT_WORKFLOW}: a red run on main would file nothing"]
    alert_text = alert.read_text(encoding="utf-8")
    watched = read_watched(alert_text)
    if not watched:
        return [
            f"{ALERT_WORKFLOW} lists no `workflow_run` workflows: the check would pass "
            "vacuously, so the parser or the file is what is broken"
        ]

    pushed_by_name: dict[str, bool] = {}
    problems: list[str] = check_alert_conclusions(alert_text)
    for path in sorted(directory.glob("*")):
        if path.suffix not in (".yml", ".yaml") or path.name == ALERT_WORKFLOW:
            continue
        text = path.read_text(encoding="utf-8")
        name = read_workflow_name(text)
        on = _key(text.splitlines(), "on", 0)
        if on is not None and on[0].startswith("{"):
            # A flow mapping would read as no push at all, so it is refused rather than passed;
            # its name still counts as carried, so the refusal is its only problem.
            if name is not None:
                pushed_by_name[name] = True
            problems.append(
                f"{path.name} writes `on:` as a flow mapping, which this check cannot read; write it as a block")
            continue
        pushed = runs_on_main_push(text)
        if name is not None:
            pushed_by_name[name] = pushed
        if not pushed:
            continue
        if name is None:
            problems.append(
                f"{path.name} runs on a push to main and has no `name:`, which is what "
                "`workflow_run` matches"
            )
        elif name not in watched:
            problems.append(
                f"{path.name} runs on a push to main and {ALERT_WORKFLOW} does not watch "
                f"{name!r}, so a red run of it alerts nobody"
            )
    if not any(pushed_by_name.values()):
        problems.append(
            "found no named workflow that runs on a push to main: the check would pass "
            "vacuously, so the parser or the directory is what is broken"
        )
    for name in watched:
        if name not in pushed_by_name:
            problems.append(f"{ALERT_WORKFLOW} watches {name!r}, which no workflow is named")
        elif not pushed_by_name[name]:
            problems.append(f"{ALERT_WORKFLOW} watches {name!r}, which never runs on a push to main")
    return problems


def read_job_condition(workflow_text: str, job: str) -> str | None:
    """One job's own four-space `if:`, folded lines joined; "" for none, None for no job.

    A step's `if:` sits deeper and is not the job's condition.
    """
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        if re.match(rf"^\s{{2}}{re.escape(job)}:\s*$", line):
            break
    else:
        return None

    condition: list[str] = []
    for line in lines[index + 1:]:
        if re.match(r"^\s{2}\S", line):          # the next job
            break
        if condition:
            if line.startswith(" " * 6):
                condition.append(line.strip())
                continue
            break
        if found := re.match(r"^\s{4}if:\s*(.*)$", line):
            condition.append(found.group(1).strip())
    return " ".join(condition).strip()


def read_job_outputs(workflow_text: str, job: str) -> set[str]:
    """The names one job exports under `outputs:`.

    A filter and a job output are different things — the first is
    `dorny/paths-filter`'s, the second is the workflow's — and only the second
    is what `needs.<job>.outputs.<name>` reads. Text again, for the reason
    every parser in this file is: no dependency, and the block is unambiguous
    at its own indentation.
    """
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        if re.match(rf"^\s{{2}}{re.escape(job)}:\s*$", line):
            break
    else:
        return set()

    found: set[str] = set()
    inside = False
    for line in lines[index + 1:]:
        if re.match(r"^\s{2}\S", line):          # the next job
            break
        if re.match(r"^\s{4}outputs:\s*$", line):
            inside = True
            continue
        if inside:
            if name := re.match(r"^\s{6}([a-z][a-z0-9-]*):\s*\S", line):
                found.add(name.group(1))
            elif line.strip() and not line.startswith(" " * 6):
                inside = False
    return found


def read_filters_by_name(workflow_text: str) -> dict[str, list[str]]:
    """The paths-filter block as {filter: [its own patterns]}.

    `read_filters` flattens, which is all the `filters` check needs — it asks
    whether a directory is reachable by any filter. Pairing needs to know
    which, so this keeps them apart. A `*shared` alias line carries no quoted
    pattern and is skipped, and that costs nothing here: every Dockerfile lives
    under its own service's tree, never under `shared`.
    """
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        match = re.match(r"^(\s*)filters:\s*\|", line)
        if match:
            break
    else:
        return {}

    indent = len(match.group(1))
    found: dict[str, list[str]] = {}
    current: str | None = None
    for line in lines[index + 1:]:
        if not line.strip():
            continue
        if len(line) - len(line.lstrip()) <= indent and not line.lstrip().startswith("#"):
            break
        if header := re.match(r"^\s+([a-z][a-z0-9-]*):\s*(?:&\w+)?\s*$", line):
            current = header.group(1)
            found.setdefault(current, [])
        elif pattern := re.match(r"\s*-\s*'([^']+)'", line):
            if current:
                found[current].append(pattern.group(1))
    return found


def _matches(pattern: str, path: str) -> bool:
    """Whether one filter pattern selects a concrete file path."""
    if pattern.startswith("!"):
        return False
    if pattern.endswith("/**"):
        return path.startswith(pattern[:-2])
    return pattern == path


# --------------------------------------------------------------------------
# shards
# --------------------------------------------------------------------------

# A dotted name, trailing dot allowed. Anything else, a pipe-joined pair for one, is a single term to the split on
# the ampersand below but not a single namespace, so it fails to match here and is reported, not read as one.
SELECTS_TERM = re.compile(r"FullyQualifiedName(!?)~([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\.?)")


def read_shards(workflow_text: str) -> list[tuple[str, str]]:
    """The `integration` job's matrix, as (shard, selects) pairs in file order.

    A `selects: >-` value is the line after it, the form a selection too long
    for one line takes without folding a space into the filter.
    """
    pairs: list[tuple[str, str]] = []
    shard: str | None = None
    body = _integration_job(workflow_text)
    for position, line in enumerate(body):
        if found := re.match(r"^\s*-\s*shard:\s*(\S+)\s*$", line):
            shard = found.group(1)
        elif found := re.match(r"^\s*selects:\s*(.*?)\s*$", line):
            value = found.group(1)
            if value == ">-" and position + 1 < len(body):
                value = body[position + 1].strip()
            if shard is not None:
                pairs.append((shard, value))
                shard = None
    return pairs


def _integration_job(workflow_text: str) -> list[str]:
    """The `integration` job's lines after its key, up to the next job."""
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        if re.match(r"^\s{2}integration:\s*$", line):
            break
    else:
        return []

    body: list[str] = []
    for line in lines[index + 1:]:
        if re.match(r"^\s{2}[^\s#]", line):      # the next job; a comment is not one
            break
        body.append(line)
    return body


def count_shard_keys(workflow_text: str) -> tuple[int, int]:
    """Every `shard:` and `selects:` key in the job, in whatever shape it is written.

    Looser than `read_shards` on purpose: a key the parser skipped is a shard
    the cascade was never checked over, and only a second count can see it.
    """
    body = _integration_job(workflow_text)
    shards = sum(1 for line in body if re.search(r"""(?:^|[\s{,])["']?shard["']?\s*:""", line))
    selects = sum(1 for line in body if re.search(r"""(?:^|[\s{,])["']?selects["']?\s*:""", line))
    return shards, selects


def check_shards() -> list[str]:
    """The integration shards partition the stage: no test in two, none in none.

    `stages` reads the shards as one directory and cannot see it, so the
    cascade is proved from the filters' text instead.
    """
    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f".github/workflows/ci.yml is not readable: {error}"]

    shards = read_shards(text)
    if not shards:
        return [
            "found no (shard, selects) pairs in the integration matrix in "
            "ci.yml, so a cascade nobody read passes vacuously — the parser is "
            "what is broken"
        ]

    problems: list[str] = []
    shard_keys, selects_keys = count_shard_keys(text)
    if (shard_keys, selects_keys) != (len(shards), len(shards)):
        problems.append(
            f"the integration job holds {shard_keys} shard and {selects_keys} "
            f"selects keys and the parser read {len(shards)} entries. Write each "
            "entry as `- shard: <name>` with its `selects:` on the line after, "
            "with no trailing comment, or the cascade is checked without it"
        )

    above: list[str] = []
    for position, (shard, selects) in enumerate(shards):
        selected: list[str] = []
        excluded: set[str] = set()
        for term in selects.split("&"):
            if found := SELECTS_TERM.fullmatch(term):
                if found.group(1):
                    excluded.add(found.group(2))
                else:
                    selected.append(found.group(2))
            else:
                problems.append(
                    f"the {shard} shard selects with {term!r}, which is not a "
                    "FullyQualifiedName term the cascade can be checked over"
                )

        if position == len(shards) - 1:
            if selected:
                problems.append(
                    f"the last shard, {shard}, selects {', '.join(selected)}. It "
                    "has to be the remainder, excluding every shard above it and "
                    "selecting nothing, or a project no shard names runs nowhere"
                )
        elif len(selected) != 1:
            problems.append(
                f"the {shard} shard selects {len(selected)} namespaces; every "
                "shard but the last selects exactly one"
            )

        if missing := sorted(set(above) - excluded):
            problems.append(
                f"the {shard} shard does not exclude {', '.join(missing)}, "
                "selected above it, so those tests run in two shards"
            )
        if extra := sorted(excluded - set(above)):
            problems.append(
                f"the {shard} shard excludes {', '.join(extra)}, which no shard "
                "above it selects, so those tests run in no shard"
            )
        above += selected

    return problems


# --------------------------------------------------------------------------
# code
# --------------------------------------------------------------------------

CODE_FILTER = "code"
STAGE_INPUTS_FILTER = "stage-inputs"
CODE_GATE = "needs.changes.outputs.code != 'false'"
# Step-level, because a matrix job skipped by its own `if:` is never expanded and a
# skipped stage skips what `needs:` it (the comment above `images`).
CODE_GATED_JOBS = ("build", "integration", "stage-gate")
# These run only behind a step that ran, so a gated job with nothing to do skips them.
STATUS_GUARD = re.compile(r"\b(?:failure|always)\(\)")
# Everything else a pull request's run needs to tell a skip from a lost answer.
CODE_OUTPUT_TERMS = (
    "github.event_name != 'pull_request'",
    "steps.changes.outputs.code != 'false'",
    "steps.changes.outputs.stage-inputs != 'false'",
)
GLOB_UNSUPPORTED = re.compile(r"[\[\]{}()+@?]")
# What the scan below can derive a search from: a whole tree, or a name at the root.
EXCLUSION_SHAPE = re.compile(r"![^*/]+(?:/[^*/]+)*/\*\*|![^/]+")


def _glob(pattern: str, path: str) -> bool:
    """`**`, `*` and literals, as picomatch reads them; `check_code_filters` refuses anything more."""
    expression = ""
    for token in re.split(r"(\*\*/|\*\*|\*)", pattern):
        expression += {"**/": "(?:.*/)?", "**": ".*", "*": "[^/]*"}.get(token, re.escape(token))
    # DOTALL: a path may hold a newline, and `**` missing it would read as no stage reads it.
    return re.fullmatch(expression, path, flags=re.DOTALL) is not None


def is_code(filters: dict[str, list[str]], path: str) -> bool:
    """Whether a path makes the two filters answer `code`, under some-with-excludes.

    An exclusion is final there, so `stage-inputs` is read on its own terms.
    """
    if any(_glob(pattern, path) for pattern in filters[STAGE_INPUTS_FILTER]):
        return True
    patterns = filters[CODE_FILTER]
    if any(_glob(pattern[1:], path) for pattern in patterns if pattern.startswith("!")):
        return False
    return any(_glob(pattern, path) for pattern in patterns if not pattern.startswith("!"))


def check_code_filters(filters: dict[str, list[str]]) -> list[str]:
    """The two filters exist and fail closed: an unlisted path is code."""
    code = filters.get(CODE_FILTER, [])
    stage_inputs = filters.get(STAGE_INPUTS_FILTER, [])
    if not code or not stage_inputs:
        return [
            f"found no `{CODE_FILTER}` or no `{STAGE_INPUTS_FILTER}` patterns in the paths-filter block, so "
            "nothing here can say what a stage reads: the parser or a filter's name is what is broken"
        ]

    problems: list[str] = []
    if code[0] != "**" or not all(pattern.startswith("!") for pattern in code[1:]):
        problems.append(
            f"the `{CODE_FILTER}` filter is not `**` and exclusions, so it lists what is code and a new "
            "path nobody listed skips every stage rather than costing a build"
        )
    for pattern in code + stage_inputs:
        if GLOB_UNSUPPORTED.search(pattern):
            problems.append(
                f"the pattern {pattern!r} holds glob syntax `_glob` does not read, so what reads these "
                "patterns here and the `changes` filter could disagree about it"
            )
    for pattern in stage_inputs:
        if pattern.startswith("!"):
            problems.append(
                f"`{STAGE_INPUTS_FILTER}` holds the exclusion {pattern!r}; it names what a stage reads, so "
                "narrowing it is how a read input stops counting"
            )
    for pattern in code[1:]:
        if not EXCLUSION_SHAPE.fullmatch(pattern):
            problems.append(
                f"the exclusion {pattern!r} is neither a whole tree nor a name at the root, so no "
                "search for what a test reads can be derived from it"
            )
    return problems


def _as_read(path: str) -> str:
    """A name with no extension is read as a directory, so a tree's own path counts as inside it."""
    return path if "." in path.rsplit("/", 1)[-1] or path.endswith("?") else f"{path}/?"


def read_tested_paths(root: Path, code: list[str]) -> list[tuple[str, str]]:
    """(source file, path) for each path a C# file or MSBuild file names under what `code` excludes.

    Such a read is how `code` could say false for a change a stage fails on. The search
    comes from the filter, so a new exclusion is scanned; a variable's part reads `?`.
    """
    excluded = [pattern.removeprefix("!") for pattern in code[1:]]
    trees = {pattern.split("/")[0] for pattern in excluded if pattern.endswith("/**")}
    heads = "|".join(re.escape(pattern.split("/")[0]).replace(r"\*", r'[^"\\/\s]*') for pattern in excluded)
    combined = re.compile(rf'"(?:(?:\.\.[\\/])+)?({heads})"((?:\s*,\s*"[^"]+")*)')
    slashed = re.compile(rf'(?:"|(?:\.\.[\\/])+)((?:{heads})[\\/][^"\s{{<>]*)')

    sources = [path for tree in ("src", "tests", "tools") for path in sorted((root / tree).rglob("*"))]
    sources += sorted(root.glob("*"))
    named: list[tuple[str, str]] = []
    for source in sources:
        if source.suffix not in {".cs", ".csproj", ".props", ".targets"} or not source.is_file():
            continue
        text = source.read_text(encoding="utf-8", errors="replace")
        where = source.relative_to(root).as_posix()
        for found in combined.finditer(text):
            before = text[max(0, found.start() - 200):found.start()].rstrip()
            # `"compose", "README.md"` is a file in that directory, not one at the root.
            if found.group(1) not in trees:
                if not (before.endswith(",") and before[:-1].rstrip().endswith('"')):
                    named.append((where, found.group(1)))
                continue
            parts = re.findall(r'"([^"]+)"', found.group(2))
            named.append((where, _as_read("/".join([found.group(1), *(parts or ["?"])]))))
        for found in slashed.finditer(text):
            named.append((where, _as_read(found.group(1).replace("\\", "/"))))
    return named


def check_code(root: Path = ROOT, workflow_text: str | None = None) -> list[str]:
    """A pull request skips the stages only for a change none of them reads."""
    if workflow_text is None:
        try:
            workflow_text = WORKFLOW.read_text(encoding="utf-8")
        except OSError as error:
            return [f".github/workflows/ci.yml is not readable: {error}"]

    filters = read_filters_by_name(workflow_text)
    problems = check_code_filters(filters)
    if CODE_FILTER not in filters or STAGE_INPUTS_FILTER not in filters:
        return problems

    export = next(
        (line for line in read_jobs(workflow_text).get("changes", []) if re.match(rf"^\s{{6}}{CODE_FILTER}:", line)),
        "",
    )
    for term in CODE_OUTPUT_TERMS:
        if term not in export:
            problems.append(
                f"the changes job's `{CODE_FILTER}` output does not read `{term}`, so it can say false for "
                "a push or a dispatch, or for a change only `stage-inputs` selects"
            )

    for job in CODE_GATED_JOBS:
        condition = read_job_condition(workflow_text, job)
        if condition:
            problems.append(
                f"the {job} job carries a job-level `if: {condition}`. Skipped there it reports no shard's "
                f"name and skips what needs it; each step's `if: {CODE_GATE}` keeps the check and passes it"
            )
        steps = read_job_steps(workflow_text, job)
        if not steps:
            problems.append(
                f"found no steps in a `{job}:` job in ci.yml: the check on them would pass vacuously, so "
                "the parser or the job's key is what is broken"
            )
        for index, step in enumerate(steps):
            guard = step.get("if", "")
            if CODE_GATE not in guard and not STATUS_GUARD.search(guard):
                name = step.get("name", step.get("uses", step.get("run", ""))).strip()[:60]
                problems.append(
                    f"the {job} job's step {index + 1} ({name!r}) has no `if: {CODE_GATE}`, so a change no "
                    "stage reads still runs it, or fails it for want of what a skipped step would have left"
                )

    named = read_tested_paths(root, filters[CODE_FILTER])
    if not named:
        problems.append(
            "found no test naming a file under what `code` excludes. Tests do read docs/ today, so the scan "
            "is what is broken, and with nothing read this check would pass vacuously"
        )
    for source, path in named:
        if not is_code(filters, path):
            problems.append(
                f"{source} names {path}, which the `{CODE_FILTER}` filter excludes and "
                f"`{STAGE_INPUTS_FILTER}` does not list, so a pull request changing it skips the test that reads it"
            )
    return problems


# --------------------------------------------------------------------------
# stages
# --------------------------------------------------------------------------

def read_stage(directory: Path) -> tuple[int, set[str], set[str]]:
    """One stage's total, its test assemblies and its test identities.

    `dotnet test` over a solution writes one TRX per test project, so this sums
    rather than reads a single number. A stage directory with no TRX in it is
    not zero tests — it is a logger that did not run, and the caller treats
    the two differently.
    """
    total = 0
    assemblies: set[str] = set()
    identities: set[str] = set()

    files = sorted(directory.rglob("*.trx"))
    if not files:
        raise SystemExit(
            f"no *.trx under {directory}. The stage did not run, or --logger trx "
            "and --results-directory disagree about where to write."
        )

    for path in files:
        try:
            root = ElementTree.parse(path).getroot()
        except ElementTree.ParseError as error:
            raise SystemExit(f"{path} is not readable as XML: {error}") from error

        # TRX carries a default namespace, so tags arrive as
        # `{...}Counters`. Matching on the local name keeps this working if the
        # schema URL is ever versioned.
        for element in root.iter():
            tag = element.tag.rsplit("}", 1)[-1]
            if tag == "Counters":
                total += int(element.get("total", "0"))
            elif tag == "TestMethod":
                # The assembly is part of the identity: two projects may hold a
                # class of the same name, and an identity without it would
                # report an overlap that is not there.
                code_base = element.get("codeBase", "")
                assembly = Path(code_base.replace("\\", "/")).stem if code_base else ""
                if assembly:
                    assemblies.add(assembly)
                identities.add(
                    f"{assembly}!{element.get('className', '')}.{element.get('name', '')}"
                )

    return total, assemblies, identities


def test_projects(solution: Path = SOLUTION) -> set[str]:
    """Every test project in the solution, by assembly name.

    `EndsWith("Tests")` and not `".Tests"`, exactly as `Directory.Build.props`
    scopes its CA1707 carve-out and for the same reason:
    `Platform.IntegrationTests` ends with the word and not the dotted suffix.
    A `.TestSupport` library is not a test project (§4.1) and is excluded by
    the same predicate without needing to be named.
    """
    text = solution.read_text(encoding="utf-8")
    return {
        name
        for name in re.findall(r'Path="[^"]*?([A-Za-z0-9.]+)\.csproj"', text)
        if name.endswith("Tests")
    }


def check_stages(directories: list[Path], solution: Path = SOLUTION) -> list[str]:
    problems: list[str] = []
    seen: dict[str, set[str]] = {}
    covered: set[str] = set()

    for directory in directories:
        stage = directory.name
        if stage not in STAGE_FLOORS:
            problems.append(
                f"{directory} is not one of the stages this gate knows "
                f"({', '.join(sorted(STAGE_FLOORS))}). A results directory nobody "
                "declared is a stage with no floor, which is the check not running"
            )
            continue

        total, assemblies, identities = read_stage(directory)
        seen[stage] = identities
        covered |= assemblies

        if total == 0:
            problems.append(
                f"the {stage} stage ran no tests. `dotnet test` exits zero on a "
                "filter that selects nothing (§12.1), so a green step says "
                "nothing about whether this stage exists"
            )
        elif total < STAGE_FLOORS[stage]:
            problems.append(
                f"the {stage} stage ran {total} tests, below its floor of "
                f"{STAGE_FLOORS[stage]}. Either the filter stopped selecting what "
                "it used to, or the floor in pipeline_gate.py is owed a "
                "deliberate edit"
            )

    if not seen:
        return problems + [
            "no stage was read, so every check here passed vacuously"
        ]

    # Every declared stage has to have been read, not merely every read stage
    # declared: drop the architecture invocation and its argument, let those
    # tests fall into the unit filter, and every check above passes while the
    # gate reports two stages ran.
    for stage in sorted(set(STAGE_FLOORS) - set(seen)):
        problems.append(
            f"the {stage} stage was never read: no results directory was passed "
            "for it. §15.1 runs three stages, and a gate that only checks the "
            "ones it was given cannot notice one going missing"
        )

    # Structural half 1: the stages partition the suite rather than sampling it.
    expected = test_projects(solution)
    if not expected:
        problems.append(
            "found no test projects in Platform.slnx: the check below would "
            "pass vacuously, so the parser is what is broken"
        )
    for project in sorted(expected - covered):
        problems.append(
            f"{project} ran in no stage. A test project the staged pipeline "
            "never selects is a suite that stopped running without anything "
            "going red"
        )

    # Structural half 2: and they do not overlap. ci.yml claims the filters are
    # disjoint by construction, and an overlap is the slow half of the suite
    # paid for twice.
    stages = sorted(seen)
    for index, first in enumerate(stages):
        for second in stages[index + 1:]:
            both = seen[first] & seen[second]
            if both:
                sample = ", ".join(identity.replace("!", " in ") for identity in sorted(both)[:3])
                problems.append(
                    f"{len(both)} test(s) ran in both the {first} and {second} "
                    f"stages, for example {sample}. The stage filters are "
                    "supposed to be disjoint, and an overlap is the suite paid "
                    "for twice"
                )

    return problems


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("filters", help="every deployable is matched by some path filter")
    sub.add_parser(
        "images",
        help="every Dockerfile is built by some matrix entry, with an SBOM, a push, a signature "
             "and two attestations, and the rollout verifies before Helm")
    sub.add_parser("shards", help="the integration shards partition the stage")
    sub.add_parser("actions", help="every third-party action is pinned to a commit")
    sub.add_parser("grants", help="no job a pull request reaches holds a write grant beside a checkout")
    sub.add_parser("alerts", help="every workflow pushed on main is watched by the red-main alert")
    sub.add_parser("code", help="a pull request skips the stages only for a change none of them reads")

    stages = sub.add_parser("stages", help="every stage ran, ran enough, and ran once")
    stages.add_argument("results", nargs="+", type=Path)

    args = parser.parse_args(argv[1:])

    if args.command == "filters":
        problems = check_filters()
        if code := fail(problems, "the path filters in ci.yml"):
            return code
        print("pipeline-gate: every deployable under src/ is matched by a filter.")
        return 0

    if args.command == "images":
        problems = check_images()
        if code := fail(problems, "the images matrix in ci.yml"):
            return code
        print(
            "pipeline-gate: every Dockerfile under src/ is built by a matrix entry, every build has an SBOM, "
            "is pushed, signed and attested on main, and the rollout verifies it before Helm."
        )
        return 0

    if args.command == "shards":
        problems = check_shards()
        if code := fail(problems, "the integration shards in ci.yml"):
            return code
        print("pipeline-gate: the integration shards are a cascade, disjoint and exhaustive.")
        return 0

    if args.command == "actions":
        problems = check_actions()
        if code := fail(problems, "the actions .github/workflows runs"):
            return code
        print("pipeline-gate: every third-party action in .github/workflows is pinned to a commit.")
        return 0

    if args.command == "grants":
        problems = check_grants()
        if code := fail(problems, "the write grants .github/workflows holds"):
            return code
        print("pipeline-gate: no job a pull request reaches holds a write grant beside a checkout.")
        return 0

    if args.command == "alerts":
        problems = check_alerts()
        if code := fail(problems, f"the workflows {ALERT_WORKFLOW} watches"):
            return code
        print(f"pipeline-gate: every workflow a push to main runs is watched by {ALERT_WORKFLOW}.")
        return 0

    if args.command == "code":
        problems = check_code()
        if code := fail(problems, "what skips the test stages in ci.yml"):
            return code
        print("pipeline-gate: a pull request skips the stages only for a change none of them reads.")
        return 0

    problems = check_stages(args.results)
    if code := fail(problems, "the test stages"):
        return code
    print(f"pipeline-gate: {len(args.results)} stages ran, none empty, none overlapping.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
