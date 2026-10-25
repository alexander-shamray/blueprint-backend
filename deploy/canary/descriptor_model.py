"""The descriptor model: canary.json and the descriptors under deployables/, and the
questions the rollout asks of them (README owns the schema)."""

from __future__ import annotations

import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]

CANARY = Path(__file__).resolve().parent

PLAN_PATH = CANARY / "canary.json"

# One descriptor per deployable, named for its release (README owns the schema).
DEPLOYABLES = CANARY / "deployables"


class PlanError(Exception):
    """The plan is unusable. Raised by the loader and by `check`."""


def entries(mapping: dict) -> dict:
    """A JSON object's real keys, with the `$comment` ones dropped.

    JSON has no comments, so the plan carries `$comment` keys; filtering them
    here keeps a call site from reading one as a workload.
    """
    return {key: value for key, value in mapping.items() if not key.startswith("$")}


def load_plan(path: Path = PLAN_PATH, deployables: Path = DEPLOYABLES) -> dict:
    """Read canary.json and every descriptor, or say what is wrong with them."""
    document = _read_object(path)
    if "workloads" in document:
        raise PlanError(
            f"{path} carries workloads, and a deployable is described by its own "
            f"file under {deployables} (deploy/canary/README.md)"
        )
    document["workloads"] = {
        descriptor.stem: _read_object(descriptor)
        for descriptor in sorted(deployables.glob("*.json"))
    }
    return document


def _read_object(path: Path) -> dict:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as error:
        raise PlanError(f"{path} is not readable: {error}") from error

    try:
        document = json.loads(text)
    except json.JSONDecodeError as error:
        raise PlanError(f"{path} is not valid JSON: {error}") from error
    if not isinstance(document, dict):
        raise PlanError(f"{path} is not a JSON object")
    return document


def workload_images(workload: str, plan_document: dict, root: Path = ROOT) -> dict[str, str]:
    """The image names a workload's chart pulls: `api`, and `migrator` if any.

    Read from the chart's own `image:` block, so no table of names is kept
    beside the chart the Deployment and the migration Job read (ADR-071).
    """
    chart = entries(plan_document.get("workloads", {})).get(workload, {}).get("chart")
    if not chart:
        raise PlanError(f"no workload {workload!r} in the plan")
    path = root / "deploy" / "helm" / chart / "values.yaml"
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as error:
        raise PlanError(f"{path} is not readable: {error}") from error
    block = re.search(r"(?m)^image:[ \t]*\r?\n((?:[ \t]+.*\r?\n?|[ \t]*\r?\n)*)", text)
    if block is None:
        raise PlanError(f"{path} has no `image:` block, so the images {workload!r} pulls are unknown")
    images = {}
    for role in ("api", "migrator"):
        found = re.search(rf"(?m)^[ \t]+{role}:[ \t]*([A-Za-z0-9][A-Za-z0-9._-]*)[ \t]*\r?$", block.group(1))
        if found:
            images[role] = found.group(1)
    if "api" not in images:
        raise PlanError(f"{path} names no image.api, so the image {workload!r} pulls is unknown")
    return images


def migrated_by(workload: str, plan_document: dict) -> str | None:
    """The workload whose chart migrates `workload`'s schema, or None for one that migrates its own.

    The rollout asks this which release a host rolls in lockstep with (ADR-118, ADR-122).
    """
    workloads = entries(plan_document["workloads"])
    if workload not in workloads:
        raise PlanError(f"no workload {workload!r} in the plan")
    named = workloads[workload].get("migratedBy")
    if named is not None and not isinstance(named, str):
        raise PlanError(f"workloads.{workload}.migratedBy is {named!r}, which names no workload")
    return named


def off_ladder(workload: str, plan_document: dict) -> bool:
    """Whether `workload` rolls in place rather than up §15.5's ladder (ADR-153).

    The descriptor's `offLadder` argues why; any other value is check 4's to
    refuse, and is read here as off, the side that runs no second pod.
    """
    workloads = entries(plan_document["workloads"])
    if workload not in workloads:
        raise PlanError(f"no workload {workload!r} in the plan")
    return "offLadder" in workloads[workload]


def telemetry_name(entry: dict) -> str:
    """The service.name a workload's release reports: its telemetryName, or its host's own (ADR-153)."""
    return entry.get("telemetryName") or entry.get("serviceName")


def migrates(workload: str, plan_document: dict) -> list[str]:
    """The workloads whose schema `workload`'s chart migrates, which ADR-122 rolls it beside."""
    workloads = entries(plan_document["workloads"])
    if workload not in workloads:
        raise PlanError(f"no workload {workload!r} in the plan")
    return [name for name, entry in workloads.items() if entry.get("migratedBy") == workload]


# A dotted path into a release's values, each segment a Helm values key.
SHARED_KEY = re.compile(r"[A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z][A-Za-z0-9]*)*")


def shared_values(workload: str, plan_document: dict) -> list[tuple[str, str]]:
    """Each (peer, key) whose value the rollout holds equal between `workload`'s release and the peer's (ADR-125).

    A pair is declared once, on either descriptor, and read from both ends,
    so the rollout of either release asks the same question.
    """
    workloads = entries(plan_document["workloads"])
    if workload not in workloads:
        raise PlanError(f"no workload {workload!r} in the plan")
    pairs = set()
    for name, entry in workloads.items():
        declared = entry.get("sharesValues")
        if not isinstance(declared, dict):
            continue
        for peer, keys in declared.items():
            other = peer if name == workload else name if peer == workload else None
            if other is None or not isinstance(keys, list):
                continue
            pairs.update((other, key) for key in keys if isinstance(key, str))
    return sorted(pairs)


# Distinct from a value of null, which a chart can state.
_ABSENT = object()


def _value_at(values: dict, key: str) -> object:
    node: object = values
    for segment in key.split("."):
        if not isinstance(node, dict) or segment not in node:
            return _ABSENT
        node = node[segment]
    return node


def _canonical(value: object) -> object:
    # A list is compared as the members it holds, in any order: every key shared today is a set the hosts read.
    if isinstance(value, list):
        return sorted({json.dumps(member, sort_keys=True) for member in value})
    return json.dumps(value, sort_keys=True) if value is not _ABSENT else "<absent>"


def values_agree(key: str, values: dict, peer_values: dict, peer: str) -> list[str]:
    """The ways `key` differs between this release's values and `peer`'s, or none (ADR-125).

    `values` is what the release will be installed with, coalesced over the checked-out chart;
    `peer_values` is the peer's `helm get values --all -o json`. Both carry chart defaults.
    """
    mine, theirs = _value_at(values, key), _value_at(peer_values, key)
    # Both carry every chart default, so a path neither holds is a misspelt key, never an agreement.
    if mine is _ABSENT and theirs is _ABSENT:
        return [f"{key} is a path neither this release's values nor {peer}'s hold, so nothing was compared"]
    mine, theirs = _canonical(mine), _canonical(theirs)
    if mine == theirs:
        return []
    if isinstance(mine, list) and isinstance(theirs, list):
        only_mine = sorted(set(mine) - set(theirs))
        only_theirs = sorted(set(theirs) - set(mine))
        return [
            f"{key} differs from {peer}'s: only this release names {', '.join(only_mine) or 'nothing'}, "
            f"and only {peer} names {', '.join(only_theirs) or 'nothing'}"
        ]
    return [f"{key} is {mine} in this release and {theirs} in {peer}'s"]


def empty_until(workload: str, plan_document: dict) -> list[tuple[str, str]]:
    """Each (key, revision) `workload`'s release keeps empty while either track serves an older revision (ADR-126)."""
    workloads = entries(plan_document["workloads"])
    if workload not in workloads:
        raise PlanError(f"no workload {workload!r} in the plan")
    declared = workloads[workload].get("emptyUntil")
    if not isinstance(declared, dict):
        return []
    return sorted((key, revision) for key, revision in declared.items() if isinstance(revision, str))


def values_named(key: str, values: dict) -> list[str]:
    """What `key` names in the values the release will be installed with, or none where it is empty (ADR-126).

    A path the values do not hold raises: they carry every chart default, so it is misspelt, never empty.
    """
    value = _value_at(values, key)
    if value is _ABSENT:
        raise PlanError(f"{key} is a path the values do not hold, so nothing was read")
    if value is None or value == "" or value == [] or value == {}:
        return []
    named = value if isinstance(value, list) else [value]
    return sorted({json.dumps(member, sort_keys=True) for member in named})
