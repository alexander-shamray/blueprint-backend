#!/usr/bin/env python3
"""§15.5's weight arithmetic and verdict, and a gate over canary.json (README).

Usage: py -3.12 deploy/canary/canary.py check | plan | analyse
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

# One descriptor per deployable, named for its release (README owns the schema).
DEPLOYABLES = CANARY / "deployables"

# Every path outside deploy/canary that this script reads, declared once;
# check 6 asserts the reads and this list agree, as deploy/helm/smoke.sh and
# deploy/observability/check.py do for their own inventories.
SOURCE_INPUTS = [
    "src",
    "deploy/helm",
    # Checks 3 and 5 both read platform-alerts.yaml: one to take §13.6's
    # thresholds from it, the other to match the series the queries read.
    # Without this entry a retuned ErrorRateService is a green pull request,
    # because observability.yml's check.py does not compare canary thresholds.
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

    Whole, because a flag and a name found anywhere on the line also match
    `--source "$OLD_IMAGE_SOURCE"`, which passes a different tree.
    """
    return f'{flag} "${variable}"'

# The two verdicts, and there are only two: a third reads as caution, but an
# unattended rollout that cannot decide leaves a canary serving on nobody's
# authority. Rollback is cheap because the canary is a second Deployment and
# the stable one is never touched (ADR-022), so every doubt resolves to it; the
# schema stays migrated, which §15.5's compatibility rule makes survivable.
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


# --------------------------------------------------------------------------
# The weight arithmetic
# --------------------------------------------------------------------------

def migration_prefix(workload: str, plan_document: dict, root: Path = ROOT) -> str | None:
    """The `<workload>-migrate-` a Job name would start with, where there is one.

    None for the gateway, which owns no database (§10.1, §15.3). Derived from
    the templates on disk, because a chart gains a migrator by having the file.
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

    `strvals` splits on a comma, so `deadbeef,image.registry=x` sets the registry
    too; the rule is `commerce.tag`'s (DNS-1123 labels, 63 characters at most).
    """
    if not tag:
        raise PlanError("image tag is empty: §15.3 refuses a deploy that cannot name its image")
    if len(tag) > 63:
        raise PlanError(
            f"image tag is {len(tag)} characters. It becomes "
            "app.kubernetes.io/version, and a label value may not exceed 63"
        )
    for segment in tag.split("."):
        if not re.fullmatch(r"[a-z0-9]([a-z0-9-]*[a-z0-9])?", segment):
            raise PlanError(
                f"image tag {tag!r} is not usable: the segment {segment!r} is not a "
                "DNS-1123 label. Each dot-separated segment must be lowercase "
                "alphanumerics and dashes, starting and ending alphanumeric — "
                "which also excludes the comma and equals that Helm's "
                "--set-string would read as a second assignment"
            )

    # `_migration-job.tpl` names the Job `<workload>-migrate-<tag>` and refuses
    # it past 63 at render time, after the stable track has been scaled up;
    # this moves that refusal in front of the scale-up (§7.4). The prefix is
    # the workload's descriptor's, so each deployable brings its own budget.
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

    Full length and lower case, because an abbreviation resolves too and the
    rollout would report a revision spelled unlike the tag (ADR-050).
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

    `helm get values --all` is the only place to read the revision behind the
    stable track's probe routes; every chart requires the tag (§15.3).
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

    The weight arithmetic counts pods and whole percentages, and the float
    route is wrong at the ladder's first input.
    """
    return -(-numerator // denominator)


def required_stable(weight_percent: int, overshoot_points: int) -> int:
    """The smallest stable replica count at which a weight is expressible.

    One canary pod serves `1 / (stable + 1)` of the traffic; inverting that
    gives 19 for §15.5's 5%. Shared with `plan` so both name one number.
    """
    if weight_percent >= 100:
        return 1
    # 100 / (stable + 1) <= weight + overshoot, in integers.
    return max(1, _ceil_div(100, weight_percent + overshoot_points) - 1)


def plan(weight_percent: int, stable_replicas: int, overshoot_points: int) -> dict:
    """How many canary pods a requested weight costs, and what it really buys.

    A pod's share is `canary / (stable + canary)` (§15.5), and the requested
    weight is a ceiling, never a target: where one pod exceeds it, this raises.
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

    # Integer arithmetic, because floats give `19 * 0.05 / 0.95` as
    # 1.0000000000000002 and `ceil` then returns two pods for 5%.
    #   canary / (stable + canary) <= weight / 100
    #     <=> canary * (100 - weight) <= weight * stable
    # Floor, then at least one pod: the largest canary within the ceiling.
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

    `readings` is `{track: {signal: {metric: value}}}` and `signals` those its
    descriptor declares. Every doubt is a rollback (§13.6, ADR-047).
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

    Both readings are present here, because `analyse` rejects an absent one
    on either track first: any absent reading is a rollback.
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
          workload: str | None = None, installed: Path | None = None) -> list[str]:
    """Every way the plan and its descriptors can be wrong, found without a cluster.

    Failures are collected, so one run reports them all. `source` is the tree
    `workload`'s image was built from; without them the checkout is read.
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
    elif workload not in workloads:
        # Including None. An unknown name matches no key below, so every
        # tree would come from the checkout and the image would answer for
        # nothing -- the scoping silently absent rather than refused.
        failures.append(
            f"a source tree was given for {workload!r}, which is not a workload "
            "in this plan, so nothing would be judged against the image "
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

    # 4. Each workload's service_name is a real host assembly: §13.2 takes
    #    service.name from the entry assembly (`Gateway.Api`, not the chart's
    #    workload.name), and a misspelling matches no series, which rolls back.
    hosts = _host_assemblies(root)
    if not workloads:
        failures.append("workloads is empty: the rollout has nothing to deploy")
    for name, entry in sorted(workloads.items()):
        # 4a. The key is a Helm release name, and two shells read it as one.
        #     `deploy.yml` passes it to `helm upgrade` and `realm.yml` uses it
        #     as a file name, so a space, glob character or slash is held out
        #     here. `fullmatch`, as `validate_tag` does: `$` accepts a key
        #     ending in a newline, an extra empty release in `workloads`.
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
        failures += _smoke_reads(name, entry, root)
    charts = [entry.get("chart") for entry in workloads.values()]
    for chart in sorted({chart for chart in charts if chart and charts.count(chart) > 1}):
        failures.append(f"two descriptors name the chart {chart!r}, and smoke.sh renders a chart once")

    # 5. Every series the query templates read is one something vouches
    #    for: a loaded alert, whose metrics check.py has proved published, or
    #    a meter Common.Web registers, at the MassTransit version the series
    #    table was verified against. A name nothing vouches for matches no
    #    series and rolls every canary back.
    failures += _metrics_are_vouched_for(sorted(series_read()), root, root)
    failures += _masstransit_pin_is_verified(root)

    #    The installed release answers for the baseline half of every
    #    comparison, and §13.2 takes service.name from each image's entry
    #    assembly. An image from before a rename emits the old label, so the
    #    baseline matches no series; refused here, before the HPA floor moves.
    if installed is not None and workload in trees:
        name = workloads[workload].get("serviceName")
        if name not in _host_assemblies(installed):
            failures.append(
                f"workloads.{workload}.serviceName is {name!r}, which the "
                "installed release's image does not build, so the baseline "
                "query would match no series and every rung would roll back"
            )

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

    # 8. Every deployable the workflow can roll, smoke.sh renders.
    failures += _descriptors_agree(workloads)

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

    # 12. The rollout hands each image's tree to everything that reads it
    #     (ADR-050), and every piece of that wiring can go missing without
    #     anything else here noticing.
    failures += _rollout_reads_the_image_source()

    return failures


# The keys the plan holds: canary.json's, and the descriptors as workloads.
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

    The error rate's numerator is coalesced, since no failures match no
    series; its denominator is not, since no traffic is judged elsewhere.
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
    otherwise; an exemption nothing needs is a claim nothing rechecks.
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
# as its assembly names them at the verified version. A service is a consumer
# by registering one, so the call is the subject. Sagas and futures are apart:
# MassTransit measures them on the saga instruments.
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

    Every character keeps its offset and line break, so a match gives its
    line and a literal is read back from the original at the same offset.
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

    Derived from the routes; refused where none is found or readable, as a
    partial exclusion counts probes as traffic.
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

    Read out of the rules file, not restated, so a retuned alert turns this
    red naming the canary that no longer agrees with it.
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

    A regex, not a YAML parser, as in check.py one tree over; anchored on the
    alert's name and stopping at the next `- alert:` or `for:`.
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

    A host has a `Program.cs` beside its csproj, which is what
    `ApplicationName` defaults to at run time; derived, never listed.
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

    A series a loaded alert reads is matched on its instrument; any other
    must be an exact EXPORTED_SERIES entry, of a meter Common.Web registers.
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
        # The workflow's own path and this gate's own tree are both required:
        # without the first, a change to the trigger lists runs no gate that
        # validates them; without `deploy/canary`, an edit to canary.py,
        # canary.json or the suite runs none either.
        for entry in SOURCE_INPUTS + ["deploy/canary", WORKFLOW_PATH]:
            if not any(p == entry or p == f"{entry}/**" for p in patterns):
                failures.append(
                    f"{WORKFLOW_PATH} trigger {index + 1} does not cover "
                    f"{entry!r}, which deploy/canary/canary.py reads"
                )
    return failures


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
    lines = [f"{chart} release {name}", f"{chart} source {source}"]
    for key in ("migrator", "autoscaled"):
        if not isinstance(smoke.get(key), bool):
            raise PlanError(
                f"workloads.{name}.smoke.{key} is {smoke.get(key)!r} rather than "
                f"true or false, so smoke.sh cannot classify the chart {chart!r}"
            )
        lines.append(f"{chart} {key} {'yes' if smoke[key] else 'no'}")
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
    """Every descriptor's smoke.sh cases, as `<chart> <field> <value>` lines."""
    return [line for name, entry in entries(workloads).items() for line in _smoke_case(name, entry)]


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


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    checker = sub.add_parser("check", help="validate the plan and its descriptors against the repository")
    # Absent on a pull request, where there is no image: the plan is then
    # checked against the checkout, which is the only tree there is.
    checker.add_argument(
        "--source", type=Path,
        help="the tree the deployed image was built from (ADR-050)")
    checker.add_argument(
        "--workload", help="whose image --source is, and the only one read from it")
    checker.add_argument(
        "--installed-source", type=Path,
        help="the tree the release being replaced was built from (ADR-050)")

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

    # One name per line, for the shell loop in which `realm.yml`'s scheduled
    # job judges each release's realm (ADR-043).
    sub.add_parser("workloads", help="every workload in the plan, one per line")

    sub.add_parser("smoke-cases", help="each deployable's smoke.sh cases, one fact per line")

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
        failures = check(document, source=args.source, workload=args.workload,
                         installed=args.installed_source)
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

    if args.command == "smoke-cases":
        try:
            cases = smoke_cases(document["workloads"])
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        if not cases:
            print(f"canary: no descriptor under {DEPLOYABLES}, so smoke.sh would render nothing", file=sys.stderr)
            return 1
        print("\n".join(cases))
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
