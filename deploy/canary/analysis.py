"""§15.5's weight arithmetic and the verdict, with the query templates the verdict
reads its signals through."""

from __future__ import annotations

import re
from pathlib import Path

from csharp_sources import probe_exclusion
from descriptor_model import PlanError, ROOT


# The two verdicts, and there are only two: a third reads as caution, but an
# unattended rollout that cannot decide leaves a canary serving on nobody's
# authority. Rollback is cheap because the canary is a second Deployment and
# the stable one is never touched (ADR-022), so every doubt resolves to it; the
# schema stays migrated, which §15.5's compatibility rule makes survivable.
PROMOTE = "promote"

ROLLBACK = "rollback"


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
