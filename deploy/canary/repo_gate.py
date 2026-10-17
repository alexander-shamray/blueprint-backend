"""The repository gate behind `canary.py check`: the plan and its descriptors held
against the C# sources, the alerts, the charts, smoke.sh and the deploy workflow."""

from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "workflow_paths"))
import workflow_paths  # noqa: E402  (ADR-132 holds the workflow-filter checks there)

from analysis import EXPORTED_SERIES, METER_INSTRUMENTS, SIGNALS, _workload_series, series_read
from csharp_sources import (
    CONSUMER_REGISTRATION, SAGA_REGISTRATION, _host_assemblies, _literal_arguments,
    _probe_routes_are_readable, _registers,
)
from descriptor_model import ROOT, SHARED_KEY, entries
from release_inputs import IMAGE_REVISION, RELEASE_NAME
from rollout_gate import (
    _rollout_follows_the_migrator, _rollout_holds_shared_values, _rollout_keeps_values_empty,
    _rollout_reads_the_image_source,
)
from workflow_reads import WORKFLOW, WORKFLOW_PATH, _descriptors_agree, _smoke_reads


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
    workflow_paths.HOME,
]


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
        failures += _migrated_by_reads(name, workloads)
        failures += _shared_values_reads(name, workloads)
        failures += _empty_until_reads(name, workloads)
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
            "hold: the queries and each signal's thresholds are analysis.py's "
            "(ADR-047)"
        )

    # 12. The rollout hands each image's tree to everything that reads it
    #     (ADR-050), and every piece of that wiring can go missing without
    #     anything else here noticing.
    failures += _rollout_reads_the_image_source()

    # 13. A workload another release migrates is rolled at that release's
    #     revision, and that release only beside it (ADR-118, ADR-122); the
    #     rollout asks the descriptors which workloads those are.
    failures += _rollout_follows_the_migrator(workloads)

    # 14. A value two releases are each given is equal in both before either
    #     rolls (ADR-125); the rollout asks the descriptors which values.
    failures += _rollout_holds_shared_values(workloads)

    # 15. A value earlier revisions misread stays empty while either track
    #     serves one (ADR-126); the rollout asks the descriptors which values.
    failures += _rollout_keeps_values_empty(workloads)

    return failures


# The keys the plan holds: canary.json's, and the descriptors as workloads.
PLAN_KEYS = {"steps", "tolerance", "thresholds", "workloads"}


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
                    f"analysis.py does not define: {', '.join(sorted(signals))}"
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


# Each registration, the signal it owes, and the key that exempts it.
OWED_SIGNALS = (
    ("consume", CONSUMER_REGISTRATION, "consumeExemption", "a consumer"),
    ("saga", SAGA_REGISTRATION, "sagaExemption", "a saga"),
)


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


def _chart_exists(chart: str | None, root: Path) -> bool:
    if not chart:
        return False
    return (root / "deploy" / "helm" / chart / "Chart.yaml").is_file()


# The MassTransit version the two tables below were read from, out of its
# package's own instrument names and units. Check 5 fails when the pin in
# Directory.Packages.props moves, because an upgrade may rename or re-unit an
# instrument and every message-judged rung would then read an absent series.
MASSTRANSIT_VERIFIED = "8.5.3"


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
            f"{props.name} pins MassTransit {pin.group(1)}, and analysis.py's "
            f"EXPORTED_SERIES was verified against {MASSTRANSIT_VERIFIED}. Re-read "
            "the package's instrument names and units, correct the table and "
            "the queries, then move MASSTRANSIT_VERIFIED"
        ]
    return []


def _workflow_covers_inputs() -> list[str]:
    """Both of the deploy workflow's triggers cover every SOURCE_INPUTS entry."""
    # The workflow's own path and `deploy/canary` are required too: without
    # them an edit to the trigger lists, canary.py or the suite runs no gate.
    return workflow_paths.check_workflow_covers_inputs(
        WORKFLOW, SOURCE_INPUTS + ["deploy/canary", WORKFLOW_PATH],
        reader="deploy/canary/canary.py", label=WORKFLOW_PATH)


def _migrated_by_reads(name: str, workloads: dict) -> list[str]:
    """Check 4 for `migratedBy`: named exactly when the chart has a database and no Job, and named right."""
    entry = workloads[name]
    smoke = entry.get("smoke") if isinstance(entry.get("smoke"), dict) else {}
    owes = smoke.get("database") is True and smoke.get("migrator") is False
    migrator = entry.get("migratedBy")
    if migrator is None:
        if owes:
            return [
                f"workloads.{name} has a database and no migration Job, and names no "
                "migratedBy, so nothing orders its rollout behind the release that "
                "migrates its schema (ADR-118)"
            ]
        return []
    if not owes:
        return [
            f"workloads.{name}.migratedBy is {migrator!r}, but its chart runs no "
            "database or migrates its own, so there is no schema for the rollout to "
            "wait on"
        ]
    other = workloads.get(migrator) if isinstance(migrator, str) else None
    other_smoke = other.get("smoke") if isinstance(other, dict) else None
    if migrator == name or not isinstance(other_smoke, dict) or other_smoke.get("migrator") is not True:
        return [
            f"workloads.{name}.migratedBy is {migrator!r}, which is not another "
            "workload whose chart runs a migration Job"
        ]
    return []


def _shared_values_reads(name: str, workloads: dict) -> list[str]:
    """Check 4 for `sharesValues`: each peer another workload, each key a values path, and each pair named once."""
    declared = workloads[name].get("sharesValues")
    if declared is None:
        return []
    if not isinstance(declared, dict) or not declared:
        return [f"workloads.{name}.sharesValues is {declared!r}, which names no peer and no key"]
    failures = []
    for peer, keys in declared.items():
        if peer == name or peer not in workloads:
            failures.append(f"workloads.{name}.sharesValues names {peer!r}, which is not another workload")
            continue
        if not isinstance(keys, list) or not keys:
            failures.append(f"workloads.{name}.sharesValues.{peer} is {keys!r}, which names no key")
            continue
        for key in keys:
            if not isinstance(key, str) or not SHARED_KEY.fullmatch(key):
                failures.append(
                    f"workloads.{name}.sharesValues.{peer} names {key!r}, which is not a dotted values path"
                )
        back = workloads[peer].get("sharesValues")
        if isinstance(back, dict) and name in back:
            failures.append(
                f"workloads.{name} and workloads.{peer} each declare sharesValues for the other, and one "
                "declaration is the pair's owner: drop one of them"
            )
    return failures


def _empty_until_reads(name: str, workloads: dict) -> list[str]:
    """Check 4 for `emptyUntil`: each key a values path and each revision a commit object name."""
    declared = workloads[name].get("emptyUntil")
    if declared is None:
        return []
    if not isinstance(declared, dict) or not declared:
        return [f"workloads.{name}.emptyUntil is {declared!r}, which names no key"]
    failures = []
    for key, revision in declared.items():
        if not SHARED_KEY.fullmatch(key):
            failures.append(f"workloads.{name}.emptyUntil names {key!r}, which is not a dotted values path")
        # Whole, since `git merge-base` resolves an abbreviation that a later commit can make ambiguous.
        if not isinstance(revision, str) or not IMAGE_REVISION.fullmatch(revision):
            failures.append(
                f"workloads.{name}.emptyUntil.{key} is {revision!r}, which is not a whole commit object name"
            )
    return failures
