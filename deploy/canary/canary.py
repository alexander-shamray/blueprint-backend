#!/usr/bin/env python3
"""§15.5's weight arithmetic and verdict, and a gate over canary.json.

Stdlib only. The arithmetic and verdict are pure; the gate and templates read
the repository, and the README says what is asserted and what is not.

    py -3.12 deploy/canary/canary.py check
    py -3.12 deploy/canary/canary.py plan --workload catalog-api --stable 19 --step 0
    py -3.12 deploy/canary/canary.py analyse --workload catalog-api --readings readings.json
"""

from __future__ import annotations

import argparse
import functools
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CANARY = Path(__file__).resolve().parent
PLAN_PATH = CANARY / "canary.json"

# EVERY PATH OUTSIDE deploy/canary THAT THIS SCRIPT READS, declared once.
#
# `deploy/helm/smoke.sh` lost count of its own inventory three times and ended
# it by declaring the list beside the reads and asserting the other copy
# matches; `deploy/observability/check.py` adopted that before paying for it
# once. This is the third tree to do it, and check 6 is the assertion.
SOURCE_INPUTS = [
    "src",
    "deploy/helm",
    # Checks 3 and 5 both read platform-alerts.yaml — one to take §13.6's
    # thresholds out of it rather than restate them, the other to establish
    # that a series the query templates read is one a loaded alert reads.
    # Retuning ErrorRateService without this entry is a green pull request:
    # observability.yml runs check.py, which does not compare canary
    # thresholds, and the canary gate never runs.
    "deploy/observability",
    # Check 5 holds the MassTransit pin to the version its series were read from.
    "Directory.Packages.props",
]

WORKFLOW_PATH = ".github/workflows/deploy.yml"
WORKFLOW = ROOT / WORKFLOW_PATH

# Where the rollout puts the image's own `src/`, and the flag that carries it.
# Named here because check 12 asserts the workflow uses both, and a gate whose
# subject is spelled twice is a gate that stops agreeing with itself.
IMAGE_SOURCE = "IMAGE_SOURCE"
INSTALLED_SOURCE = "INSTALLED_SOURCE"
SOURCE_FLAG = "--source"
INSTALLED_FLAG = "--installed-source"


def _argument(flag: str, variable: str) -> str:
    """The whole argument check 12 looks for, spelled once.

    Whole, because a flag and a name found anywhere on the line are also
    found in `--source "$OLD_IMAGE_SOURCE"`, which passes a different tree
    and would leave the check green while the binding was gone.
    """
    return f'{flag} "${variable}"'

# The two verdicts, and there are deliberately only two.
#
# A third — "hold", "inconclusive", "needs a human" — reads as caution and is
# the opposite: an unattended rollout that cannot decide leaves a canary
# serving traffic on nobody's authority. The reason this is affordable is the
# shape of the mechanism rather than optimism about the readings. The canary is
# a SECOND Deployment and the stable one is never touched (ADR-022), so
# rollback costs the canary's own pods and nothing else — no `helm rollback`
# and no image change on the pods serving the other 95%.
#
# NOT "and no schema to undo", which this comment said and ADR-022 denies: the
# canary release runs §7.4's migration hook, because it is the first thing
# carrying the new image, and a rollback removes the pods and LEAVES THE SCHEMA
# MIGRATED. What makes that survivable is §15.5's backward-compatibility
# requirement, which ADR-022 sharpens rather than relaxes — a cheap rollback is
# worth nothing against an incompatible migration. The pods are the cheap half;
# the schema is not a half this mechanism buys at all.
#
# When rollback is cheap, every doubt resolves to it.
PROMOTE = "promote"
ROLLBACK = "rollback"

# Helm's release-name alphabet: a DNS-1123 label, at most 53 characters so the
# suffixed resource names still fit 63. Every workload key is one, and `check`
# holds it to this before a shell reads it as a word or a path.
RELEASE_NAME = re.compile(r"^[a-z0-9]([-a-z0-9]{0,51}[a-z0-9])?$")


class PlanError(Exception):
    """The plan is unusable. Raised by the loader and by `check`."""


def entries(mapping: dict) -> dict:
    """A JSON object's real keys, with the `$comment` ones dropped.

    JSON has no comments and this plan needs them: every number in it is a
    decision somebody has to be able to re-take. `$comment` is the convention
    the tooling around JSON Schema already uses, and it is filtered HERE rather
    than at each call site because forgetting it once turns a comment into a
    workload with no service name.
    """
    return {key: value for key, value in mapping.items() if not key.startswith("$")}


def load_plan(path: Path = PLAN_PATH) -> dict:
    """Read canary.json, or explain what is wrong with it."""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as error:
        raise PlanError(f"{path} is not readable: {error}") from error

    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        raise PlanError(f"{path} is not valid JSON: {error}") from error


# --------------------------------------------------------------------------
# The weight arithmetic
# --------------------------------------------------------------------------

def migration_prefix(workload: str, plan_document: dict, root: Path = ROOT) -> str | None:
    """The `<workload>-migrate-` a Job name would start with, where there is one.

    None for a chart that renders no migration Job — the gateway and the BFF
    own no database (§10.1, §15.3), so their tags are bounded only by the label
    length. Derived from the templates on disk rather than listed, because a
    sixth service's chart gains a migrator by having the file.
    """
    workloads = entries(plan_document.get("workloads", {}))
    chart = workloads.get(workload, {}).get("chart")
    if not chart:
        return None
    if not (root / "deploy" / "helm" / chart / "templates" / "migrate-job.yaml").is_file():
        return None
    return f"{workload}-migrate-"


def validate_tag(tag: str, job_prefix: str | None = None) -> None:
    """Refuse a tag Helm's `--set-string` would read as more than a tag.

    **`--set-string image.tag="$TAG"` is not a single assignment**, and that is
    the finding this exists for. Helm parses the right-hand side with `strvals`,
    where a COMMA separates assignments — so
    `deadbeef,image.registry=attacker.example` sets a perfectly valid
    `image.tag` AND overrides the registry, for the canary and the promotion
    alike. `commerce.tag`'s render-time validation passes, because by then the
    tag really is `deadbeef`; the injected key rode in beside it.

    This is docs/lessons.md's own lesson about a value crossing between two
    systems' alphabets, one release later: the tag is validated against Kubernetes'
    alphabet at render time and reaches Helm's parser before that.
    **Validate against the intersection, at the boundary the value enters.**

    The rule is `commerce.tag`'s, deliberately — dot-separated DNS-1123 labels,
    63 characters at most — because a second, looser alphabet here would let
    something through that the chart then rejects mid-`helm upgrade`. It admits
    no comma, no equals and no whitespace, which is what closes the injection;
    that is a consequence of matching the chart rather than a rule of its own,
    and it is the reason this cannot be relaxed independently.
    """
    if not tag:
        raise PlanError("image tag is empty: §15.3 refuses a deploy that cannot name its image")
    if len(tag) > 63:
        raise PlanError(
            f"image tag is {len(tag)} characters. It becomes "
            "app.kubernetes.io/version, and a label value may not exceed 63 (§15.3)"
        )
    for segment in tag.split("."):
        if not re.fullmatch(r"[a-z0-9]([a-z0-9-]*[a-z0-9])?", segment):
            raise PlanError(
                f"image tag {tag!r} is not usable: the segment {segment!r} is not a "
                "DNS-1123 label. Each dot-separated segment must be lowercase "
                "alphanumerics and dashes, starting and ending alphanumeric "
                "(§15.3) — which also excludes the comma and equals that Helm's "
                "--set-string would read as a second assignment"
            )

    # The migration Job's name is a tighter budget than the label's on any
    # chart that has one: `_migration-job.tpl` derives
    # `<workload>-migrate-<tag>` and refuses it past 63 at render time, which
    # on this path is after the stable track has been scaled up. Moving that
    # refusal in front of the scale-up is the one job this preflight has.
    # The prefix comes from canary.json's workload map rather than being
    # written down, so a workload added there brings its own budget with it,
    # and the longest prefix among them sets the tightest one.
    if job_prefix is not None and len(job_prefix) + len(tag) > 63:
        raise PlanError(
            f"image tag {tag!r} is {len(tag)} characters, and the migration Job "
            f"would be named {job_prefix + tag!r} at {len(job_prefix) + len(tag)}. "
            "Kubernetes copies that onto every pod as the `job-name` label, which "
            f"may not exceed 63, so this workload allows {63 - len(job_prefix)} "
            "(§7.4). Refused here rather than by `helm upgrade` after the stable "
            "track has been scaled up"
        )


# §15.2 builds both of a service's images with the commit as the tag, so a
# tag here is a commit object name. ADR-050 is why the rollout insists on it:
# every fact this script derives from `src/` describes the running image, and
# it can only be read from the revision that image was built from.
IMAGE_REVISION = re.compile(r"^[0-9a-f]{40}$")


def image_revision(tag: str) -> str:
    """The commit an image tag names, or a refusal.

    Full length and lower case, because the answer is resolved against this
    repository's history and then printed. An abbreviation resolves too, and
    the rollout would report a revision spelled differently from the tag it
    deployed -- which is the confusion ADR-050 exists to remove.
    """
    if not IMAGE_REVISION.fullmatch(tag):
        raise PlanError(
            f"the image tag {tag!r} names no revision. §15.2 tags every image "
            "with the commit it was built from, and §15.5's rollout reads that "
            "image's probe routes, registrations and entry assembly out of the "
            "same commit (ADR-050). A tag that is not a commit leaves all three "
            "read from whatever the runner happened to check out"
        )
    return tag


def installed_tag(values: dict) -> str:
    """The image tag the running release was installed with.

    `helm get values --all` is the only place the revision behind the stable
    track's probe routes can be read, because that track serves whatever it
    was installed with and no input names it. Every chart requires the tag
    (§15.3), so a release without one is one this rollout cannot reason
    about rather than a default to fall back on.
    """
    tag = values.get("image", {}).get("tag")
    if not isinstance(tag, str) or not tag.strip():
        raise PlanError(
            "the installed release names no image.tag, so the revision its "
            "probe routes come from cannot be read (ADR-050)"
        )
    return tag.strip()


def _ceil_div(numerator: int, denominator: int) -> int:
    """Integer ceiling division, because `math.ceil` on a float lies here.

    Every quantity in the weight arithmetic is a count of pods or a whole
    percentage, so the exact answer is available and the float route is not
    merely imprecise — it is wrong at the input the ladder starts from.
    """
    return -(-numerator // denominator)


def required_stable(weight_percent: int, overshoot_points: int) -> int:
    """The smallest stable replica count at which a weight is expressible.

    One canary pod is the smallest canary there is, so it serves
    `1 / (stable + 1)` of the traffic and that fraction is the finest weight
    the mechanism has. Inverting it gives the stable count a requested weight
    needs — 19 for §15.5's 5%, which is why the ladder's first rung is a
    scale-up and not a no-op.

    Separate from `plan` and used by it, so the number in the refusal and the
    number the workflow scales to are the same number. Two derivations of one
    figure is how a message ends up naming a count that does not satisfy the
    check that printed it.
    """
    if weight_percent >= 100:
        return 1
    # 100 / (stable + 1) <= weight + overshoot, in integers.
    return max(1, _ceil_div(100, weight_percent + overshoot_points) - 1)


def plan(weight_percent: int, stable_replicas: int, overshoot_points: int) -> dict:
    """How many canary pods a requested weight costs, and what it really buys.

    **A replica-weighted canary cannot hit an arbitrary weight**, and this is
    the function that refuses to pretend otherwise. Traffic reaches these pods
    through a ClusterIP Service, which spreads connections across its endpoints
    — so the share the new version serves is `canary / (stable + canary)` and
    the achievable weights are the fractions that arithmetic can make. With
    §15.3's `replicaCount: 3`, the smallest canary is one pod and the smallest
    weight is 25%, which is five times the 5% §15.5 asks for.

    **The requested weight is a ceiling, not a target to land on.** The canary
    is the LARGEST one whose share stays within it, which is the only direction
    that is safe to be wrong in: undershooting means a smaller blast radius
    than was asked for, and overshooting means more traffic on the new version
    than anybody authorised. A step labelled 5% that serves 25% is the failure
    this whole function exists to prevent, and rounding to the nearest
    expressible weight is how it would have happened.

    One pod is the floor, so where even a single canary exceeds the ceiling
    there is nothing to round down to and this raises. The message names the
    stable replica count that WOULD satisfy the request, because that is the
    decision the operator actually has — scale up and pay for it, or accept a
    coarser step and say so in `tolerance`.

    This rule and `required_stable` are one design read from two ends: that
    function answers "how many stable pods make ONE canary fit", which is
    precisely the boundary at which this stops raising. They disagreed once,
    and the test that pairs them is what said so.
    """
    if not 0 < weight_percent <= 100:
        raise PlanError(f"weight must be in (0, 100]; got {weight_percent}")
    if stable_replicas < 1:
        raise PlanError(f"stable replicas must be at least 1; got {stable_replicas}")

    if weight_percent == 100:
        # The last rung is not a weight, it is the end of the rollout: the
        # canary becomes the release. Expressing it as pods would ask for an
        # infinite canary against a stable track that is about to go away.
        return {
            "requested": 100,
            "canaryReplicas": stable_replicas,
            "stableReplicas": 0,
            "achieved": 100.0,
            "final": True,
        }

    # INTEGER ARITHMETIC THROUGHOUT, and that is a correction rather than a
    # preference. Written with floats this read
    # `ceil(stable * f / (1 - f))`, and at the one input the whole ladder
    # starts from — 5% against 19 replicas — `19 * 0.05 / 0.95` evaluates to
    # 1.0000000000000002, so `ceil` returned two pods and the step served 9.5%
    # instead of 5%. `required_stable` and `plan` then disagreed about the same
    # number: one named 19 as the count that works and the other refused it.
    # Found by the test asserting those two agree.
    #
    #   canary / (stable + canary) <= weight / 100
    #     <=> canary * (100 - weight) <= weight * stable
    #
    # Floor, then a minimum of one pod: the largest canary that stays within
    # the ceiling, or the smallest canary there is when none does.
    canary = max(1, (weight_percent * stable_replicas) // (100 - weight_percent))
    achieved = 100 * canary / (stable_replicas + canary)

    # Compared as integers for the same reason: `achieved` is a ratio and the
    # question is whether 100 * canary exceeds (weight + overshoot) of the
    # total, which has an exact answer.
    if 100 * canary > (weight_percent + overshoot_points) * (stable_replicas + canary):
        needed = required_stable(weight_percent, overshoot_points)
        raise PlanError(
            f"{weight_percent}% is not reachable with {stable_replicas} stable "
            f"replicas: one canary pod already serves {achieved:.1f}%, which "
            f"overshoots by more than {overshoot_points} points. A "
            f"replica-weighted canary quantises the weight (ADR-022). Either "
            f"scale stable to {needed} first, or start the ladder at a weight "
            f"this replica count can express."
        )

    return {
        "requested": weight_percent,
        "canaryReplicas": canary,
        "stableReplicas": stable_replicas,
        "achieved": round(achieved, 2),
        "final": False,
    }


# --------------------------------------------------------------------------
# The verdict
# --------------------------------------------------------------------------

TRACKS = ("canary", "baseline")
METRICS = ("errorRate", "latencyP99Seconds", "requests")

# The thresholds a signal may name in `absolute`, and how each is printed.
# Both are §13.6's alert numbers, which check 3 reads out of the rules file.
ABSOLUTE = {"errorRate": ("error rate", "{:.3%}"), "latencyP99Seconds": ("p99", "{:.3f}s")}


def analyse(readings: dict, thresholds: dict, signals: dict) -> dict:
    """Promote or roll back, from one step's readings of one workload's signals.

    `readings` is `{track: {signal: {metric: value}}}` and `signals` is the
    workload's declared subset of canary.json's `signals`. An absent, quiet,
    missing or undeclared signal is a rollback, like every other doubt: an
    empty series reads the same whether nothing failed or nothing was scraped
    (§13.6). Each declared signal is then held to its own `absolute`
    thresholds and compared with the stable track (ADR-047).
    """
    for track in TRACKS:
        if not isinstance(readings.get(track), dict):
            return _verdict(ROLLBACK, f"no readings for the {track} track")

    if not signals:
        return _verdict(
            ROLLBACK, "the workload declares no signal, so there is nothing to judge it on")

    for track in TRACKS:
        present = set(readings[track])
        undeclared = sorted(present - set(signals))
        if undeclared:
            return _verdict(
                ROLLBACK,
                f"the {track} track carries readings for {', '.join(undeclared)}, "
                "which this workload does not declare: the fetch and the plan "
                "disagree about what is being judged",
            )
        missing = sorted(set(signals) - present)
        if missing:
            return _verdict(
                ROLLBACK,
                f"the {track} track has no {', '.join(missing)} readings, and a "
                "declared signal nobody fetched is not one that passed",
            )

    for track in TRACKS:
        for signal in sorted(signals):
            values = readings[track][signal]
            for name in METRICS:
                if not isinstance(values, dict) or values.get(name) is None:
                    return _verdict(
                        ROLLBACK,
                        f"the {track} track reported no {signal} {name}: the "
                        "series is absent, which is what a metric nobody "
                        "publishes and a pod nobody scraped look alike"
                        "(§13.6)",
                    )

    minimum = thresholds["minimumRequests"]
    for signal in sorted(signals):
        counted = readings["canary"][signal]["requests"]
        if counted < minimum:
            return _verdict(
                ROLLBACK,
                f"the canary's {signal} signal counted {counted:.0f} in the "
                f"step's window, below the {minimum} this plan calls enough to "
                "judge. Promoting on that is promoting on no evidence",
            )

    verdicts = []
    factor = thresholds["regressionFactor"]
    for signal, definition in sorted(signals.items()):
        canary = readings["canary"][signal]
        baseline = readings["baseline"][signal]
        for key in definition.get("absolute", []):
            label, fmt = ABSOLUTE[key]
            if canary[key] > thresholds[key]:
                # §13.6's alerts read the HTTP series alone, so only there
                # is the breached number one that pages.
                pages = " that pages" if signal == "http" else ""
                verdicts.append(
                    f"{signal} {label} {fmt.format(canary[key])} is above the "
                    f"{fmt.format(thresholds[key])}{pages} (§13.6)"
                )
        verdicts += _regression(
            f"{signal} error rate",
            canary["errorRate"],
            baseline["errorRate"],
            thresholds["errorRateFloor"],
            factor,
            "{:.3%}",
        )
        verdicts += _regression(
            f"{signal} p99",
            canary["latencyP99Seconds"],
            baseline["latencyP99Seconds"],
            thresholds["latencyP99FloorSeconds"],
            factor,
            "{:.3f}s",
        )

    if verdicts:
        return _verdict(ROLLBACK, "; ".join(verdicts))

    judged = "; ".join(
        f"{signal}: error rate {readings['canary'][signal]['errorRate']:.3%} and "
        f"p99 {readings['canary'][signal]['latencyP99Seconds']:.3f}s over "
        f"{readings['canary'][signal]['requests']:.0f}"
        for signal in sorted(signals)
    )
    return _verdict(
        PROMOTE,
        f"{judged} - every signal inside its thresholds and none materially "
        "worse than the stable track",
    )


def _regression(
    label: str,
    canary_value: float,
    baseline_value: float | None,
    floor: float,
    factor: float,
    fmt: str,
) -> list[str]:
    """One metric's relative check.

    **Both readings are present by the time this runs**, because `analyse`
    rejects an absent one on either track before applying any threshold. That
    is a change: a missing baseline used to skip this check, on the argument
    that the canary's absence means the new version is unobserved while the
    baseline's only means there is nothing to compare against.

    It did not survive. The stable track serves the MAJORITY of traffic at
    every rung of §15.5's ladder, so its series going missing is the monitoring
    failing on the larger half — and since the error-rate numerator is
    coalesced, a query returns nothing only when the DENOMINATOR is empty, no
    requests at all. Skipping removed regression detection at exactly that
    moment. The rule is now uniform and therefore statable: **any absent
    reading is a rollback**, with no exception to remember and none to check
    for here.
    """
    if canary_value <= floor:
        return []
    if canary_value <= baseline_value * factor:
        return []
    return [
        f"{label} {fmt.format(canary_value)} is more than {factor}x the stable "
        f"track's {fmt.format(baseline_value)}"
    ]


def _verdict(decision: str, reason: str) -> dict:
    return {"decision": decision, "reason": reason}


def _shout(key: str) -> str:
    """`canaryReplicas` -> `CANARY_REPLICAS`. Shell variables are not camel."""
    return re.sub(r"(?<!^)(?=[A-Z])", "_", key).upper()


# --------------------------------------------------------------------------
# The gate over the plan
# --------------------------------------------------------------------------

def check(plan_document: dict, root: Path = ROOT, source: Path | None = None,
          workload: str | None = None) -> list[str]:
    """Everything that can be wrong with canary.json without a cluster.

    Failures are collected rather than raised, so one run reports them all.
    Some checks guard the gate rather than the plan: its own subject, the
    workflow's path filter, its dispatch menu, and ADR-050's binding. `source`
    is the tree the deployed image was built from and `workload` is whose
    image it is; without them every fact is read from the checkout.
    """
    failures: list[str] = []

    steps = plan_document.get("steps", [])
    thresholds = entries(plan_document.get("thresholds", {}))
    workloads = entries(plan_document.get("workloads", {}))

    # A tag names one workload's image, so only that workload is read from it
    # (ADR-050). The others are running images built from other revisions,
    # and judging their plan entries against this one refuses rollouts that
    # are fine.
    if source is None:
        trees = dict.fromkeys(workloads, root)
        source = root
    elif workload is None:
        failures.append(
            "a source tree was given without the workload whose image it is, "
            "so every workload would be judged against one image's revision "
            "(ADR-050)"
        )
        trees = dict.fromkeys(workloads, root)
        source = root
    else:
        trees = {name: (source if name == workload else root) for name in workloads}

    # 1. The ladder climbs, ends at 100, and dwells.
    if not steps:
        failures.append("steps is empty: a rollout with no steps promotes nothing")
    else:
        weights = [step.get("weight") for step in steps]
        if weights != sorted(weights) or len(set(weights)) != len(weights):
            failures.append(
                f"steps must have strictly increasing weights; got {weights}"
            )
        if weights[-1] != 100:
            failures.append(
                f"the last step is {weights[-1]}%, not 100%: a ladder that stops "
                "short leaves the old version serving traffic for ever"
            )
        for step in steps[:-1]:
            if not step.get("dwellMinutes"):
                failures.append(
                    f"the {step.get('weight')}% step has no dwell: §15.5 watches "
                    "each weight for ten minutes, and a step with no window is a "
                    "step whose query has nothing to average over"
                )

    # 2. Every threshold the verdict reads exists. `analyse` indexes these
    #    rather than `.get`-ing them, so a missing key is a KeyError mid-rollout
    #    — which is this check's whole reason for existing.
    for key in (
        "errorRate",
        "latencyP99Seconds",
        "errorRateFloor",
        "latencyP99FloorSeconds",
        "regressionFactor",
        "minimumRequests",
    ):
        if key not in thresholds:
            failures.append(f"thresholds.{key} is missing; analyse() reads it")

    # 3. The absolute thresholds are §13.6's, and that is asserted rather than
    #    intended. A canary tolerating what pages the on-call has bought
    #    nothing, and the two numbers drifting apart is invisible from either
    #    side.
    failures += _thresholds_match_alerts(thresholds, root)

    # 4. Each workload's service_name is a real host assembly.
    #
    #    §13.2 sets the resource's service.name from
    #    `builder.Environment.ApplicationName`, which defaults to the ENTRY
    #    ASSEMBLY name — so the edge emits `Gateway.Api` and the chart's
    #    `workload.name` (`gateway`) never reaches the label.
    #    platform-alerts.yaml carries a nine-line comment about getting this
    #    exact substitution wrong, where the misspelling matched no series and
    #    the alert was silent. The same misspelling here promotes every canary,
    #    because a query that matches nothing returns nothing and an absent
    #    series is the rollback above — so it fails safe and never promotes,
    #    which is a rollout that can only ever roll back.
    hosts = _host_assemblies(root)
    if not workloads:
        failures.append("workloads is empty: the rollout has nothing to deploy")
    for name, entry in sorted(workloads.items()):
        # 4a. The key is a Helm release name, and two shells read it as one.
        #     `deploy.yml` passes it to `helm upgrade` and `realm.yml`'s
        #     scheduled job reads it off `workloads` one line at a time, then
        #     uses it as a file name under RUNNER_TEMP. A key with a space, a
        #     glob character or a slash would be two releases, an expansion
        #     against the checkout, or a path — so the key is held to the
        #     alphabet Helm holds a release to, here, before either job sees it.
        #     `fullmatch`, as `validate_tag` already uses: `match` with a `$`
        #     anchor accepts a key ending in a newline, which the line-oriented
        #     `workloads` output would emit as an extra, empty release.
        if not RELEASE_NAME.fullmatch(name):
            failures.append(
                f"workloads.{name!r} is not a Helm release name: lower-case "
                "letters, digits and hyphens, starting and ending with a letter "
                "or digit, at most 53 characters"
            )
        service_name = entry.get("serviceName")
        known = _host_assemblies(trees[name])
        if service_name not in known:
            failures.append(
                f"workloads.{name}.serviceName is {service_name!r}, which is not "
                f"an entry assembly in this solution. §13.2 takes service.name "
                f"from ApplicationName, so it must be one of: "
                f"{', '.join(sorted(known))}"
            )
        if not _chart_exists(entry.get("chart"), root):
            failures.append(
                f"workloads.{name}.chart is {entry.get('chart')!r}, which is "
                "not a chart under deploy/helm"
            )

    # 5. Every series the query templates read is one something vouches
    #    for: a loaded alert, whose metrics check.py has proved published, or
    #    a meter Common.Web registers, at the MassTransit version the series
    #    table was verified against. A name nothing vouches for matches no
    #    series and rolls every canary back.
    failures += _metrics_are_vouched_for(sorted(series_read()), root, root)
    failures += _masstransit_pin_is_verified(root)

    #    The rolled workload's own signals are vouched for against its image
    #    too, and only its own: series_read() spans every signal the plan
    #    defines, so a gateway image that receives no messages would be
    #    refused for lacking a MassTransit meter it has no reason to carry.
    if workload in trees and trees[workload] is not root:
        failures += _metrics_are_vouched_for(
            sorted(_workload_series(workloads[workload])), root, trees[workload])

    # 6. The gate's own subject. Checks 3, 4 and 5 all compare against
    #    something parsed out of another file, and a parser that quietly
    #    extracted nothing would pass all three vacuously — which is this
    #    repository's most-repeated failure, named in CLAUDE.md as such.
    for label, tree in (("the checkout", root), ("the image", source)):
        if not _host_assemblies(tree):
            failures.append(
                f"found no host assemblies under {label}'s src/: check 4 would "
                "pass vacuously, so the parser is what is broken rather than "
                "the plan"
            )

    # 7. The workflow's triggers cover every input this rollout reads.
    failures += _workflow_covers_inputs()

    # 8. The dispatch menu is exactly the plan's workload set.
    failures += _dispatch_options_match_workloads(workloads)

    # 9. Each workload declares the signals it receives: a service that
    #    registers a consumer or a saga declares consume or saga, or argues
    #    why not (ADR-047).
    failures += _workloads_declare_what_they_receive(workloads, SIGNALS, trees)

    # 10. The probe routes the http templates exclude can all be read.
    failures += _probe_routes_are_readable(source)

    # 11. The plan holds data and no query text: the templates are code.
    for key in sorted(set(entries(plan_document)) - PLAN_KEYS):
        failures.append(
            f"canary.json has a top-level {key!r}, which the plan does not "
            "hold: the queries and each signal's thresholds are canary.py's "
            "(ADR-047)"
        )

    # 12. The rollout hands the image's tree to everything that reads it
    #     (ADR-050). Two arguments and one exported path, each of which can go
    #     missing without anything else here noticing.
    failures += _rollout_reads_the_image_source()

    return failures


# The keys canary.json holds.
PLAN_KEYS = {"steps", "tolerance", "thresholds", "workloads"}

# The signals ADR-047 defines, with the absolute thresholds each is held to.
# Only http names p99, because only a request has a latency alert to be held
# to; a message signal's duration is judged against the stable track alone.
# A message signal names the MassTransit instrument its series come from.
SIGNALS = {
    "http": {"absolute": ("errorRate", "latencyP99Seconds")},
    "consume": {"absolute": ("errorRate",), "instrument": "messaging.masstransit.consume"},
    "saga": {"absolute": ("errorRate",), "instrument": "messaging.masstransit.saga"},
}

# ASP.NET Core's request histogram (§13.2), in seconds, which check 5 vouches
# for through the alerts that read it.
HTTP_HISTOGRAM = "http_server_request_duration_seconds"

# What turns a histogram's unit into the seconds analyse compares.
TO_SECONDS = {"s": "", "ms": " / 1000"}

# Every selector's workload and track matchers, as equality: the one form in
# which a query reads exactly one workload's one track.
WORKLOAD_AND_TRACK = 'service_name="$SERVICE", deployment_track="$TRACK"'


def queries(signal: str, source: Path = ROOT) -> dict[str, str]:
    """The three PromQL templates for `signal`, one per role analyse reads.

    Each carries $SERVICE, $TRACK and $WINDOW for read_prometheus to fill.
    The error rate coalesces its numerator, because a canary with no failures
    matches no numerator series and PromQL carries the empty vector through
    the division; the denominator is not, because no traffic is a silence
    that requests and minimumRequests judge.
    """
    series = _series_of(signal)
    selector = WORKLOAD_AND_TRACK
    failed = selector
    if signal == "http":
        selector += f', http_route!~"{probe_exclusion(source)}"'
        failed = selector + ', http_response_status_code=~"5.."'
    return {
        "errorRate": (
            f"(sum(rate({series['faults']}{{{failed}}}[$WINDOW])) or vector(0)) "
            f"/ sum(rate({series['attempts']}{{{selector}}}[$WINDOW]))"
        ),
        "latencyP99Seconds": (
            f"histogram_quantile(0.99, sum by (le) "
            f"(rate({series['bucket']}{{{selector}}}[$WINDOW])))"
            f"{TO_SECONDS[series['unit']]}"
        ),
        "requests": f"sum(increase({series['attempts']}{{{selector}}}[$WINDOW]))",
    }


def _series_of(signal: str) -> dict[str, str]:
    """The series a signal's templates read: attempts, faults and the
    duration bucket, and the bucket's unit."""
    if signal == "http":
        return {
            "attempts": f"{HTTP_HISTOGRAM}_count",
            "faults": f"{HTTP_HISTOGRAM}_count",
            "bucket": f"{HTTP_HISTOGRAM}_bucket",
            "unit": "s",
        }
    instrument = SIGNALS[signal]["instrument"]
    by_instrument = {
        (entry[1], name.rsplit("_", 1)[-1]): (name, entry[3])
        for name, entry in EXPORTED_SERIES.items()
    }
    bucket, unit = by_instrument[(f"{instrument}.duration", "bucket")]
    return {
        "attempts": by_instrument[(instrument, "total")][0],
        "faults": by_instrument[(f"{instrument}.errors", "total")][0],
        "bucket": bucket,
        "unit": unit,
    }


def _workload_series(workload: dict) -> set[str]:
    """Every series the templates read for the signals this workload declares."""
    return {
        name
        for signal in workload.get("signals", [])
        if signal in SIGNALS
        for role, name in _series_of(signal).items()
        if role != "unit"
    }


def series_read() -> set[str]:
    """Every series any signal's templates read."""
    return {
        name
        for signal in SIGNALS
        for role, name in _series_of(signal).items()
        if role != "unit"
    }


def _workloads_declare_what_they_receive(workloads: dict, signals: dict,
                                         trees: dict[str, Path]) -> list[str]:
    """Every workload declares a known signal, and each registration its signal.

    A consumer owes consume and a saga owes saga, unless an exemption argues
    otherwise. An exemption has to be needed: on a workload with nothing to
    exempt, or beside the signal it exempts, it is a claim nothing rechecks.
    """
    failures = []
    for name, workload in sorted(workloads.items()):
        declared = workload.get("signals", [])
        if not declared:
            failures.append(
                f"workloads.{name} declares no signal, and analyse() rolls back "
                "a workload with nothing to judge"
            )
        for signal in declared:
            if signal not in signals:
                failures.append(
                    f"workloads.{name} declares the signal {signal!r}, which "
                    f"canary.py does not define: {', '.join(sorted(signals))}"
                )

        # Every workload is an ASP.NET Core host, so its HTTP traffic is
        # judged unless the plan argues why not, and only then.
        http_exemption = workload.get("httpExemption")
        if "http" in declared:
            if http_exemption is not None:
                failures.append(
                    f"workloads.{name}.httpExemption sits beside a declared http "
                    "signal, and one of the two is wrong"
                )
        elif not isinstance(http_exemption, str) or not http_exemption.strip():
            failures.append(
                f"workloads.{name} declares no http signal and argues no "
                "httpExemption, so its HTTP endpoints are unjudged by omission"
            )

        for signal, pattern, key, kind in OWED_SIGNALS:
            registers = _registers(workload.get("serviceName", ""), pattern, trees[name])
            exemption = workload.get(key)
            if exemption is None:
                if registers and signal not in declared:
                    failures.append(
                        f"workloads.{name} registers {kind} and declares no "
                        f"{signal} signal, so that broker work is not judged. "
                        f"Declare it, or argue a {key}"
                    )
            elif not isinstance(exemption, str) or not exemption.strip():
                failures.append(
                    f"workloads.{name}.{key} is empty, which is an exemption "
                    "without an argument"
                )
            elif not registers:
                failures.append(
                    f"workloads.{name}.{key} exempts a service that registers "
                    f"no {kind.split()[-1]}"
                )
            elif signal in declared:
                failures.append(
                    f"workloads.{name}.{key} sits beside a declared {signal} "
                    "signal, and one of the two is wrong"
                )
            if signal in declared and not registers:
                failures.append(
                    f"workloads.{name} declares the {signal} signal and registers "
                    f"no {kind.split()[-1]}, so its series cannot exist and every "
                    "rung would roll back"
                )
    return failures


# The MassTransit registration methods a scan can find, singly and in bulk,
# as MassTransit's own assembly names them at the verified version. A service
# is a consumer by registering one, and the common consumers live in
# Common.Infrastructure, so the call is the subject rather than an IConsumer<T>
# implementation. Sagas and futures, which are state machines, are apart
# because MassTransit measures them on the saga instruments.
CONSUMER_METHODS = (
    "AddConsumer", "AddConsumers", "AddConsumersFromNamespaceContaining",
    "AddFutureRequestConsumer",
)
SAGA_METHODS = (
    "AddSaga", "AddSagas", "AddSagasFromNamespaceContaining",
    "AddSagaStateMachine", "AddSagaStateMachines",
    "AddSagaStateMachinesFromNamespaceContaining", "AddJobSagaStateMachines",
    "AddFuture", "AddFutures", "AddFuturesFromNamespaceContaining",
)


def _registration(methods: tuple[str, ...]) -> re.Pattern:
    """A call to any of `methods`, generic or not, and to nothing longer."""
    return re.compile(rf"\.(?:{'|'.join(methods)})\s*[<(]")


CONSUMER_REGISTRATION = _registration(CONSUMER_METHODS)
SAGA_REGISTRATION = _registration(SAGA_METHODS)

# Each registration, the signal it owes, and the key that exempts it.
OWED_SIGNALS = (
    ("consume", CONSUMER_REGISTRATION, "consumeExemption", "a consumer"),
    ("saga", SAGA_REGISTRATION, "sagaExemption", "a saga"),
)


def has_consumers(service_name: str, root: Path = ROOT) -> bool:
    """Whether the service whose host is `service_name` registers a consumer."""
    return _registers(service_name, CONSUMER_REGISTRATION, root)


def has_sagas(service_name: str, root: Path = ROOT) -> bool:
    """Whether the service whose host is `service_name` registers a saga."""
    return _registers(service_name, SAGA_REGISTRATION, root)


@functools.cache
def _registers(service_name: str, pattern: re.Pattern, root: Path) -> bool:
    """Whether any source in the service's tree matches `pattern`.

    The service's tree is the host project's parent directory, which holds
    the host and the projects it composes.
    """
    sources = _csharp_sources(root)
    for host in (path for path in sources if path.name == f"{service_name}.csproj"):
        tree = host.parent.parent
        for path, code in sources.items():
            if path.suffix == ".cs" and tree in path.parents and pattern.search(
                    _code_only(code)):
                return True
    return False


# A literal is matched before a comment, so a `//` or `/*` inside one stays
# text: raw, verbatim, regular and character literals, then the two comments.
# A raw literal closes on a run of exactly as many quotes as opened it.
CSHARP_TOKEN = re.compile(
    r'(?P<literal>\$*(?P<run>"{3,})[\s\S]*?(?P=run)|\$?@\$?"(?:[^"]|"")*"|\$?"(?:\\.|[^"\\\n])*"'
    r"|'(?:\\.|[^'\\\n])')"
    r"|(?P<comment>//[^\n]*|/\*[\s\S]*?\*/)"
)


@functools.cache
def _code_only(code: str) -> str:
    """C# with its comments and the contents of its literals blanked.

    Every character keeps its offset and every line break stays, so a match
    in the result gives its line, and a literal argument is read back from
    the original text at the same offset. Cached, as the scans repeat.
    """
    return CSHARP_TOKEN.sub(lambda token: re.sub(r"[^\n]", " ", token.group(0)), code)


# One ordinary literal with no escape, closing its argument. A literal the
# argument continues past, as a concatenation, is not the whole value, and an
# escape spells a value the source text does not show.
WHOLE_LITERAL = re.compile(r"\s*\"([^\"\\\n]+)\"(?=\s*[,)])")


def _literal_arguments(code: str, method: str) -> list[tuple[str | None, int]]:
    """Each live call to `method`, with its first argument where that is a
    whole literal, and the call's offset. The call is found in the masked
    text, so a comment or a string that spells one is not a call."""
    masked = _code_only(code)
    calls = []
    for call in re.finditer(rf"\b{method}\s*\(", masked):
        literal = WHOLE_LITERAL.match(code, call.end())
        calls.append((literal.group(1) if literal else None, call.start()))
    return calls


@functools.cache
def _csharp_sources(root: Path) -> dict[Path, str]:
    """Every project file and C# source under src/, outside build output.

    Cached because the scans run once per workload and per check; a project
    file maps to an empty text, since only its name is read.
    """
    found = {}
    for path in (root / "src").rglob("*"):
        if {"obj", "bin"} & set(path.relative_to(root).parts):
            continue
        if path.suffix == ".cs":
            found[path] = path.read_text(encoding="utf-8")
        elif path.suffix == ".csproj":
            found[path] = ""
    return found


def health_routes(root: Path = ROOT) -> set[str]:
    """Every literal route a host maps a health check on, read from the code."""
    return {route for route, _ in _health_calls(root) if route is not None}


def unresolved_health_routes(root: Path = ROOT) -> list[str]:
    """Every MapHealthChecks call whose route is not a literal, as file:line."""
    return [site for route, site in _health_calls(root) if route is None]


@functools.cache
def _health_calls(root: Path) -> tuple[tuple[str | None, str], ...]:
    """Each MapHealthChecks call site outside a comment, with its literal."""
    calls = []
    for path, code in sorted(_csharp_sources(root).items()):
        if path.suffix != ".cs":
            continue
        for route, offset in _literal_arguments(code, "MapHealthChecks"):
            line = code.count("\n", 0, offset) + 1
            calls.append((route, f"{path.relative_to(root).as_posix()}:{line}"))
    return tuple(calls)


def probe_exclusion(source: Path = ROOT) -> str:
    """The http_route regex that excludes every probe route the code maps.

    Derived from the routes rather than written, so a new probe route is
    excluded the day it is mapped. Refused where the scan found none or
    cannot read one, because a partial exclusion counts probes as traffic.
    """
    failures = _probe_routes_are_readable(source)
    if failures:
        raise PlanError("; ".join(failures))
    # PromQL anchors the regex at both ends; its string literal needs each
    # backslash re.escape adds doubled.
    return "|".join(re.escape(route).replace("\\", "\\\\") for route in sorted(health_routes(source)))


def _probe_routes_are_readable(source: Path) -> list[str]:
    """The probe-route scan found routes, and every call site's is a literal.

    The subject is checked first, because a scan that found no route would
    exclude nothing and certify it.
    """
    if not health_routes(source):
        return ["found no MapHealthChecks route under src/: the probe exclusion "
                "would be empty, so the scan is what is broken"]
    return [
        f"{site} maps a health check on a route that is not a string literal, "
        "so the probe exclusion cannot include it"
        for site in unresolved_health_routes(source)
    ]


def _thresholds_match_alerts(thresholds: dict, root: Path) -> list[str]:
    """The canary's absolute thresholds against §13.6's loaded rules.

    Read out of the rules file rather than restated, so the two cannot part.
    The alert expressions end in `> 0.01` and `> 1`; those are the numbers, and
    if somebody retunes an alert this goes red naming the canary that no longer
    agrees with it.
    """
    rules = root / "deploy" / "observability" / "alerts" / "platform-alerts.yaml"
    try:
        text = rules.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{rules} is not readable, so the thresholds cannot be checked: {error}"]

    failures = []
    for alert, key, label in (
        ("ErrorRateService", "errorRate", "error rate"),
        ("Latency", "latencyP99Seconds", "p99"),
    ):
        expected = _alert_threshold(text, alert)
        if expected is None:
            failures.append(
                f"could not read the {alert} threshold out of {rules.name}: the "
                f"canary's {label} cannot be checked against an alert it cannot find"
            )
        elif key in thresholds and thresholds[key] != expected:
            failures.append(
                f"thresholds.{key} is {thresholds[key]} and {alert} fires at "
                f"{expected}. A canary that tolerates what pages promotes a "
                "release and then wakes somebody about it"
            )
    return failures


def _alert_threshold(text: str, alert: str) -> float | None:
    """The comparison at the end of one alert's expression.

    The rules are YAML and this is a regex, for the reason check.py one tree
    over gives: there is no stdlib YAML parser, and the alternative to matching
    text is a dependency this gate must not have. The pattern is anchored on
    the alert's own name and stops at the next `- alert:` or `for:`, so it
    cannot drift onto a neighbour's number.
    """
    block = re.search(
        rf"- alert:\s*{re.escape(alert)}\s*\n(.*?)(?=\n\s*(?:- alert:|for:))",
        text,
        re.DOTALL,
    )
    if not block:
        return None
    comparisons = re.findall(r">\s*([0-9]+(?:\.[0-9]+)?)\s*$", block.group(1), re.MULTILINE)
    if len(comparisons) != 1:
        return None
    return float(comparisons[0])


def _host_assemblies(source: Path) -> set[str]:
    """Every project that produces a host, by assembly name.

    A host is a project with a `Program.cs` beside its csproj — which is what
    `Assembly.GetEntryAssembly()` resolves to at run time and therefore what
    `ApplicationName` defaults to. Derived rather than listed, so a sixth
    service's API is a host here the day it exists.
    """
    hosts = set()
    for csproj in (source / "src").rglob("*.csproj"):
        if (csproj.parent / "Program.cs").exists():
            hosts.add(csproj.stem)
    return hosts


def _chart_exists(chart: str | None, root: Path) -> bool:
    if not chart:
        return False
    return (root / "deploy" / "helm" / chart / "Chart.yaml").is_file()


# The MassTransit version the two tables below were read from, out of its
# package's own instrument names and units. Check 5 fails when the pin in
# Directory.Packages.props moves, because an upgrade may rename or re-unit an
# instrument and every message-judged rung would then read an absent series.
MASSTRANSIT_VERIFIED = "8.5.3"

# The instruments of a third-party meter this plan reads, as the verified
# version declares them. They are real only while Common.Web collects the
# meter, which is the registration `_metrics_are_vouched_for` looks for.
METER_INSTRUMENTS = {
    "MassTransit": (
        "messaging.masstransit.consume",
        "messaging.masstransit.consume.errors",
        "messaging.masstransit.consume.duration",
        "messaging.masstransit.saga",
        "messaging.masstransit.saga.errors",
        "messaging.masstransit.saga.duration",
    ),
}

# Every exported series of those instruments a query may read, exactly:
# series -> (meter, instrument, kind, unit). A spelling missing here is one
# the instrument does not export, however near its prefix.
EXPORTED_SERIES = {
    "messaging_masstransit_consume_ea_total":
        ("MassTransit", "messaging.masstransit.consume", "Counter", "ea"),
    "messaging_masstransit_consume_errors_ea_total":
        ("MassTransit", "messaging.masstransit.consume.errors", "Counter", "ea"),
    "messaging_masstransit_consume_duration_milliseconds_bucket":
        ("MassTransit", "messaging.masstransit.consume.duration", "Histogram", "ms"),
    "messaging_masstransit_saga_ea_total":
        ("MassTransit", "messaging.masstransit.saga", "Counter", "ea"),
    "messaging_masstransit_saga_errors_ea_total":
        ("MassTransit", "messaging.masstransit.saga.errors", "Counter", "ea"),
    "messaging_masstransit_saga_duration_milliseconds_bucket":
        ("MassTransit", "messaging.masstransit.saga.duration", "Histogram", "ms"),
}

# The OTLP-to-Prometheus unit suffixes: a known UCUM unit is spelled out, and
# an unknown one such as ea is appended as it stands.
UNIT_SUFFIX = {"s": "seconds", "ms": "milliseconds"}


def exported_series(instrument: str, kind: str, unit: str) -> set[str]:
    """The series one instrument exports, under the OTLP-to-Prometheus mapping."""
    base = instrument.replace(".", "_") + (f"_{UNIT_SUFFIX.get(unit, unit)}" if unit else "")
    if kind == "Histogram":
        return {f"{base}_bucket", f"{base}_count", f"{base}_sum"}
    if kind == "Counter":
        return {f"{base}_total"}
    return {base}


def _metrics_are_vouched_for(metrics: list[str], root: Path, source: Path) -> list[str]:
    """Every series in `metrics`, against what vouches for it.

    A metric a loaded alert reads is matched on its instrument, because an
    alert may read `_count` where a query reads `_bucket` off one histogram.
    Any other must be an exact entry in EXPORTED_SERIES, of a meter Common.Web
    registers.
    """
    rules = root / "deploy" / "observability" / "alerts" / "platform-alerts.yaml"
    registration = source / "src" / "BuildingBlocks" / "Common.Web" / "ObservabilityExtensions.cs"
    try:
        alert_text = rules.read_text(encoding="utf-8")
        registered_text = registration.read_text(encoding="utf-8")
    except OSError as error:
        return [f"a file check 5 reads is not readable, so no metric can be vouched for: {error}"]

    registered = {
        meter for meter, _ in _literal_arguments(registered_text, "AddMeter")
    } & set(METER_INSTRUMENTS)

    failures = []
    for metric in metrics:
        if metric in EXPORTED_SERIES:
            meter, instrument, _, _ = EXPORTED_SERIES[metric]
            if instrument not in METER_INSTRUMENTS.get(meter, ()):
                failures.append(
                    f"a template reads {metric}, whose instrument {instrument} "
                    f"is not one {meter} {MASSTRANSIT_VERIFIED} declares"
                )
            elif meter not in registered:
                failures.append(
                    f"a template reads {metric}, from the {meter} meter, and "
                    f"{registration.name} does not register "
                    f"AddMeter(\"{meter}\"), so nothing collects it"
                )
            continue
        base = re.sub(r"_(?:count|bucket|sum|total)$", "", metric)
        if base not in alert_text:
            failures.append(
                f"a template reads {metric}, which no loaded alert reads and "
                "which is not an exported series in EXPORTED_SERIES, so "
                "nothing establishes that it is published"
            )
    return failures


# The pin the series table is held to.
MASSTRANSIT_PIN = re.compile(r"<PackageVersion\s+Include=\"MassTransit\"\s+Version=\"([^\"]+)\"")


def _masstransit_pin_is_verified(root: Path) -> list[str]:
    """The MassTransit pin is the version EXPORTED_SERIES was verified against."""
    props = root / "Directory.Packages.props"
    try:
        text = props.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{props.name} is not readable, so the MassTransit pin cannot be checked: {error}"]
    pin = MASSTRANSIT_PIN.search(text)
    if not pin:
        return [f"{props.name} pins no MassTransit version, so the series table "
                "cannot be held to one"]
    if pin.group(1) != MASSTRANSIT_VERIFIED:
        return [
            f"{props.name} pins MassTransit {pin.group(1)}, and canary.py's "
            f"EXPORTED_SERIES was verified against {MASSTRANSIT_VERIFIED}. Re-read "
            "the package's instrument names and units, correct the table and "
            "the queries, then move MASSTRANSIT_VERIFIED"
        ]
    return []


def _workflow_covers_inputs() -> list[str]:
    """Both of the deploy workflow's triggers cover every SOURCE_INPUTS entry.

    A merged change that skips the gate on `main` is the same defect one branch
    later, which is why both triggers are checked rather than the first.
    """
    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable: {error}"]

    # `on:` has one `paths:` per trigger. Anything else means the workflow was
    # restructured and this check no longer knows what it is reading.
    blocks = re.findall(r"paths:\s*\n((?:\s*-\s*'[^']+'\s*\n)+)", text)
    if len(blocks) != 2:
        return [
            f"{WORKFLOW_PATH} has {len(blocks)} path lists, expected two (one per "
            "trigger). This check cannot say whether the gate's inputs are "
            "covered, which is not the same as saying they are"
        ]

    failures = []
    for index, block in enumerate(blocks):
        patterns = re.findall(r"-\s*'([^']+)'", block)
        # THE WORKFLOW'S OWN PATH AND THIS GATE'S OWN TREE ARE BOTH REQUIRED,
        # and each was missing in turn. Without the workflow, removing it from
        # both trigger lists means a change to those very lists no longer runs
        # the gate validating them. Without `deploy/canary`, removing THAT
        # means an edit to `canary.py`, `canary.json` or the suite does not run
        # the gate either — the tree that holds the thing being checked, gone
        # from the triggers, with the check still green.
        #
        # `check.py` has required both since it was written
        # (`SOURCE_INPUTS + ["deploy/observability", WORKFLOW_PATH]`); this
        # copy inherited the pattern one piece at a time.
        for entry in SOURCE_INPUTS + ["deploy/canary", WORKFLOW_PATH]:
            if not any(p == entry or p == f"{entry}/**" for p in patterns):
                failures.append(
                    f"{WORKFLOW_PATH} trigger {index + 1} does not cover "
                    f"{entry!r}, which deploy/canary/canary.py reads"
                )
    return failures


def _dispatch_options_match_workloads(workloads: dict) -> list[str]:
    """The `workload:` dispatch input's `options:` against canary.json's keys.

    An exact set, not a subset either way: an option the plan cannot roll
    dispatches a release `chart` and `plan` have never heard of, a workload
    missing from the list is one a manual dispatch cannot choose, and nothing
    else compares the two. Parsed as a flow sequence on `_alert_threshold`'s
    terms, and scoped to `workload:`'s own child indentation so that a
    sibling input's `options:` cannot stand in for this input's own.
    """
    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable, so its dispatch options cannot be checked: {error}"]

    block = re.search(r"(?m)^([ \t]*)workload:\n((?:\1[ \t].*\n?)*)", text)
    child_indent = block and re.match(r"[ \t]+", block.group(2))
    options_match = child_indent and re.search(
        rf"(?m)^{re.escape(child_indent.group(0))}options:\s*\[([^\]]*)\]", block.group(2)
    )
    if not options_match:
        return [
            f"{WORKFLOW_PATH} has no options list for the workload dispatch "
            "input, so a manual rollout cannot be checked against the plan"
        ]

    options = {item.strip() for item in options_match.group(1).split(",") if item.strip()}
    expected = set(workloads)

    failures = []
    missing = expected - options
    if missing:
        failures.append(
            f"{WORKFLOW_PATH}'s workload dispatch options omit "
            f"{', '.join(sorted(missing))}: canary.json can roll them and a "
            "manual dispatch cannot choose them"
        )
    extra = options - expected
    if extra:
        failures.append(
            f"{WORKFLOW_PATH}'s workload dispatch options list "
            f"{', '.join(sorted(extra))}, which is not a workload in canary.json"
        )
    return failures


def _invocations(lines: list[str], command: str) -> list[str]:
    """Each run of `command` in `lines`, with its continuations joined.

    A workflow spells a long command over several lines ending in a
    backslash, so the arguments that matter are not on the line the command
    is named on. Joining is what lets the check below ask about a whole
    invocation rather than about a line.
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


def _rollout_reads_the_image_source(workflow: Path = WORKFLOW) -> list[str]:
    """The rollout hands the image's tree to everything that reads `src/`.

    ADR-050's binding is one exported path and two arguments, and dropping
    any of them restores the defect in silence: the commands still run, this
    gate still passes, and every source-derived fact is the checkout's again.
    So the subject here is the workflow text rather than a verdict, because
    an argument that went missing is not observable from one.
    """
    try:
        text = workflow.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable, so ADR-050's binding is unchecked: {error}"]

    # Comment lines are dropped first: this file argues at length about the
    # commands it runs, and a check that read those would pass on the prose.
    live = [line for line in text.splitlines() if not line.lstrip().startswith("#")]

    failures = []
    for variable in (IMAGE_SOURCE, INSTALLED_SOURCE):
        # The name is matched whole. `IMAGE_SOURCE=` is also inside
        # `OLD_IMAGE_SOURCE=`, so a consistent rename would satisfy a
        # substring test while exporting a different tree.
        assignment = re.compile(rf"(?<![A-Za-z0-9_]){variable}=")
        if not any(assignment.search(line) and "GITHUB_ENV" in line for line in live):
            failures.append(
                f"no step exports {variable} into GITHUB_ENV, so the rollout has "
                "no tree to read it from and ADR-050's binding is not in force"
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
    gate = _argument(SOURCE_FLAG, IMAGE_SOURCE)
    if not any(gate in run for run in _invocations(live, "canary.py check")):
        failures.append(
            f"no `canary.py check` run takes {gate}, so the plan is never held "
            "against the source the deployed image was built from (ADR-050)"
        )
    return failures


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    checker = sub.add_parser("check", help="validate canary.json against the repository")
    # Absent on a pull request, where there is no image: the plan is then
    # checked against the checkout, which is the only tree there is.
    checker.add_argument(
        "--source", type=Path,
        help="the tree the deployed image was built from (ADR-050)")
    checker.add_argument(
        "--workload", help="whose image --source is, and the only one read from it")

    revision = sub.add_parser("revision", help="the commit an image tag names")
    revision.add_argument("--value", required=True)

    installed = sub.add_parser(
        "installed-tag", help="the tag the running release was installed with")
    installed.add_argument(
        "--values", required=True, type=Path,
        help="a `helm get values --all -o json` document")

    # The workflow asks this BEFORE the first step, because §15.5's 5% is not
    # expressible at §15.3's replicaCount of 3 and scaling up is the operator's
    # decision rather than a surprise mid-rollout. One number, on stdout, so a
    # shell can read it without a JSON parser.
    required = sub.add_parser("required", help="stable replicas the first step needs")
    required.add_argument("--step", type=int, default=0)

    sub.add_parser("steps", help="how many rungs the ladder has")

    tag = sub.add_parser("validate-tag", help="refuse a tag Helm would read as two assignments")
    tag.add_argument("--value", required=True)
    tag.add_argument("--workload", help="apply that workload's migration Job name budget too")

    # Separate from `plan` on purpose: the chart a workload deploys is a fact
    # about the plan, and `plan` REFUSES when the step's weight is not
    # expressible at the current replica count. Asking it for a chart would
    # make resolving the chart depend on the arithmetic succeeding.
    chart = sub.add_parser("chart", help="the chart directory a workload deploys")
    chart.add_argument("--workload", required=True)

    # One name per line, for a shell loop. `realm.yml`'s scheduled job reads
    # every release's authority between rollouts (ADR-043), and the set of
    # releases is this plan's — a list restated in that workflow would agree
    # with this one until a fifth workload joined here and not there, which is
    # `deploy.yml`'s `options:` list one artefact over, and that one at least
    # is a dispatch menu rather than a subject.
    sub.add_parser("workloads", help="every workload in the plan, one per line")

    planner = sub.add_parser("plan", help="canary replicas for one step")
    planner.add_argument("--workload", required=True)
    planner.add_argument("--stable", type=int, required=True)
    planner.add_argument("--step", type=int, required=True)

    analyser = sub.add_parser("analyse", help="promote or roll back")
    analyser.add_argument("--workload", required=True, help="whose declared signals to judge")
    analyser.add_argument("--readings", required=True, help="path to a readings JSON file")

    args = parser.parse_args(argv[1:])

    try:
        document = load_plan()
    except PlanError as error:
        print(f"canary: {error}", file=sys.stderr)
        return 1

    if args.command == "installed-tag":
        try:
            document = json.loads(args.values.read_text(encoding="utf-8"))
            print(installed_tag(document))
        except (OSError, json.JSONDecodeError) as error:
            print(f"canary: {args.values} is not readable JSON: {error}", file=sys.stderr)
            return 1
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "revision":
        try:
            print(image_revision(args.value))
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        return 0

    if args.command == "check":
        failures = check(document, source=args.source, workload=args.workload)
        if failures:
            print(f"canary: {len(failures)} problem(s) with the rollout plan:\n", file=sys.stderr)
            for failure in failures:
                print(f"  - {failure}", file=sys.stderr)
            return 1
        print(f"canary: the plan is consistent - {len(document['steps'])} steps, "
              f"{len(entries(document['workloads']))} workloads.")
        return 0

    if args.command == "steps":
        print(len(document["steps"]))
        return 0

    if args.command == "validate-tag":
        prefix = migration_prefix(args.workload, document) if args.workload else None
        try:
            validate_tag(args.value, prefix)
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        print(args.value)
        return 0

    if args.command == "chart":
        workloads = entries(document["workloads"])
        if args.workload not in workloads:
            print(f"canary: no workload {args.workload!r} in the plan", file=sys.stderr)
            return 1
        print(workloads[args.workload]["chart"])
        return 0

    if args.command == "workloads":
        for name in entries(document["workloads"]):
            print(name)
        return 0

    if args.command == "required":
        try:
            step = document["steps"][args.step]
        except IndexError:
            print(f"canary: no step {args.step} in the plan", file=sys.stderr)
            return 1
        print(required_stable(
            step["weight"],
            document["tolerance"]["weightOvershootPoints"],
        ))
        return 0

    if args.command == "plan":
        if args.workload not in entries(document["workloads"]):
            print(f"canary: no workload {args.workload!r} in the plan", file=sys.stderr)
            return 1
        try:
            step = document["steps"][args.step]
        except IndexError:
            print(f"canary: no step {args.step} in the plan", file=sys.stderr)
            return 1
        try:
            result = plan(
                step["weight"],
                args.stable,
                document["tolerance"]["weightOvershootPoints"],
            )
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        workload = entries(document["workloads"])[args.workload]
        result["dwellMinutes"] = step.get("dwellMinutes", 0)
        result["chart"] = workload["chart"]

        # `KEY=value` rather than JSON, so the caller is `eval`-able from a
        # shell and needs no parser. The workflow that drives this is bash on a
        # runner; handing it JSON would put five inline `python -c` invocations
        # in a YAML string, and every one of them is a place for a quoting bug
        # in the one file nothing here can test.
        for key, value in result.items():
            print(f"CANARY_{_shout(key)}={value}")
        return 0

    workloads = entries(document["workloads"])
    if args.workload not in workloads:
        print(f"canary: no workload {args.workload!r} in the plan", file=sys.stderr)
        return 1
    declared = {
        signal: SIGNALS.get(signal, {})
        for signal in workloads[args.workload].get("signals", [])
    }
    readings = json.loads(Path(args.readings).read_text(encoding="utf-8"))
    verdict = analyse(readings, entries(document["thresholds"]), declared)
    print(json.dumps(verdict, indent=2))
    return 0 if verdict["decision"] == PROMOTE else 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
