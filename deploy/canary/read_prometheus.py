#!/usr/bin/env python3
"""Run a workload's declared queries against Prometheus and write the result.

The one file here that talks to anything, so that `canary.py` can decide with
a suite. It never interprets: no series is `null`, never a zero, and
`analyse` reads `null` as a rollback (§13.6). Stdlib `urllib` only.

    py -3.12 deploy/canary/read_prometheus.py --workload catalog-api --window 10m --out readings.json
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from canary import entries, load_plan

TIMEOUT_SECONDS = 30


def query(base_url: str, expression: str) -> float | None:
    """One instant query, or None when there is nothing to read.

    None covers three different silences on purpose: an empty result, a
    non-finite value, and a body that does not parse. All three mean the same
    thing to the caller — this metric was not measured — and distinguishing
    them here would invite a caller to treat one of them as a reading.

    A transport or HTTP error is NOT one of them. That is the monitoring stack
    being unreachable rather than the canary being unobserved, and a rollout
    that cannot see its own monitoring must stop rather than guess; it raises.
    """
    url = f"{base_url.rstrip('/')}/api/v1/query?" + urllib.parse.urlencode({"query": expression})
    with urllib.request.urlopen(url, timeout=TIMEOUT_SECONDS) as response:  # noqa: S310
        raw = response.read()

    # The docstring above promises that a body which does not parse is one of
    # the silences, and it was not: `json.loads` and the UTF-8 decode both
    # raised straight past this function. A proxy returning an HTML error page
    # with a 200 is the ordinary way that happens.
    try:
        body = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None

    if body.get("status") != "success":
        raise RuntimeError(f"Prometheus refused the query: {body.get('error', body)}")

    result = body.get("data", {}).get("result", [])
    if not result:
        return None

    try:
        value = float(result[0]["value"][1])
    except (KeyError, IndexError, TypeError, ValueError):
        return None

    # EVERY non-finite value, not just NaN. `histogram_quantile` returns NaN for
    # a histogram with no observations, and NaN compares false against every
    # threshold — so read as a number it sails through the absolute and the
    # relative checks alike and promotes.
    #
    # Prometheus also encodes `+Inf` and `-Inf`, and `-Inf` is the dangerous
    # one: it is BELOW every absolute threshold and below any multiple of the
    # baseline, so it is not merely admitted, it looks excellent. `!= self` only
    # catches NaN, which is how the first version of this line covered a third
    # of what its own comment claimed.
    return value if math.isfinite(value) else None


def read(base_url: str, workload: str, window: str, plan: dict) -> dict:
    """Both tracks' readings of the signals `workload` declares, and no others.

    The service name is the plan's, so it has one spelling. An unknown
    workload raises KeyError rather than reading nothing.
    """
    entry = entries(plan["workloads"])[workload]
    definitions = entries(plan["signals"])
    readings: dict[str, dict[str, dict[str, float | None]]] = {}
    for track in ("canary", "baseline"):
        # `baseline` is the verdict's name for the stable track, and `stable`
        # is the label's.
        label = "stable" if track == "baseline" else "canary"
        readings[track] = {
            signal: {
                name: query(
                    base_url,
                    expression
                    .replace("$SERVICE", entry["serviceName"])
                    .replace("$TRACK", label)
                    .replace("$WINDOW", window),
                )
                for name, expression in entries(definitions[signal]["queries"]).items()
            }
            for signal in entry.get("signals", [])
        }
    return readings


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workload", required=True, help="a workload key in canary.json")
    parser.add_argument("--window", required=True, help="the step's dwell, as a PromQL duration")
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args(argv[1:])

    base_url = os.environ.get("PROMETHEUS_URL")
    if not base_url:
        # Blank counts as missing, which is a lesson this repository learned
        # against an environment variable three times.
        print("read_prometheus: PROMETHEUS_URL is unset", file=sys.stderr)
        return 1

    try:
        readings = read(base_url, args.workload, args.window, load_plan())
    except KeyError as error:
        print(f"read_prometheus: no workload or signal {error} in the plan", file=sys.stderr)
        return 1
    except (urllib.error.URLError, RuntimeError, OSError) as error:
        print(f"read_prometheus: {error}", file=sys.stderr)
        return 1

    args.out.write_text(json.dumps(readings, indent=2), encoding="utf-8")
    print(json.dumps(readings, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
