"""docs/runbooks/latency.md. Alert: Latency, p99 over 1s on a 10-minute window, `for: 10m`, under the traffic loop.
Cause:      `pause sql` under the loop, held past the `for:`; every request that reaches a database waits for its
            command to time out, and the requests held complete as the slow ones the quantile reads.
First step: the runbook's per-route p99 query as written, and which of its two shapes the answer has.
Restore:    unpause sql, then wait for the slow requests to leave the 10-minute window and the alert to resolve.
"""

from __future__ import annotations

import harness

RUNBOOK = "latency.md"
ALERT = "Latency"
FOR_SECONDS = 600
# A request held by a paused database ends when its command times out (30 seconds), and the quantile needs two of them
# in about a hundred (one in a hundred is the threshold): the first completes after the timeout and the second three
# seconds of loop later, so a minute is the ceiling for the signal.
DEADLINE = harness.Deadline(signal_seconds=60, for_seconds=FOR_SECONDS)
# The slow requests leave a 10-minute window ten minutes after the last of them completes.
SETTLE = harness.Deadline(signal_seconds=harness.LATENCY_WINDOW_SECONDS)
TRAFFIC_SECONDS = 600 + DEADLINE.seconds + SETTLE.seconds + 300
WARM_SECONDS = 2 * harness.EXPORT_INTERVAL_SECONDS + harness.EVALUATION_INTERVAL_SECONDS + 60

# The runbook's query, verbatim apart from its layout.
FIRST_QUERY = ('histogram_quantile(0.99, sum by (service_name, http_route, le) ('
               'rate(http_server_request_duration_seconds_bucket[10m])))')
SERVICE_QUERY = ('histogram_quantile(0.99, sum by (service_name, le) ('
                 'rate(http_server_request_duration_seconds_bucket[10m])))')


def cause(world: harness.World) -> None:
    world.traffic.start(TRAFFIC_SECONDS)
    # A quantile has to be readable and under 1s first, or "it rose" is not what the alert fired on.
    harness.wait_until(_quiet_quantile(world), WARM_SECONDS, "the p99 reading under 1s from the loop's requests")
    world.compose.pause("sql")
    world.say("sql paused; requests that reach a database now wait for their command to time out")


def _quiet_quantile(world: harness.World):
    def read() -> tuple[bool, str]:
        rows = world.alerts.query(SERVICE_QUERY)
        values = {row["metric"].get("service_name"): float(row["value"][1]) for row in rows}
        ordering = values.get("Ordering.Api")
        return ordering is not None and ordering < 1, f"p99 by service: {values}"
    return read


def first_step(world: harness.World) -> tuple[bool, str]:
    slow = [(row["metric"].get("service_name"), row["metric"].get("http_route"), float(row["value"][1]))
            for row in world.alerts.query(FIRST_QUERY) if row["value"][1] != "NaN" and float(row["value"][1]) > 1]
    if not slow:
        return False, "the runbook's per-route query reads no route over 1s"
    services = sorted({service for service, _, _ in slow})
    shape = ("everything slow together, which the runbook sends to *Everything is slow*" if len(services) > 1
             else "one service slow, which the runbook reads as one route")
    worst = max(slow, key=lambda row: row[2])
    return True, (f"{len(slow)} routes over 1s in {', '.join(services)}; worst {worst[0]} {worst[1]} "
                  f"{worst[2]:.1f}s; {shape}")


def restore(world: harness.World) -> None:
    world.compose.unpause("sql")


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
