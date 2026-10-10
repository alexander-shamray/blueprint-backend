"""docs/runbooks/queue-backlog.md (the lag half; the backlog half stays OWED). Alert: DeliveryLagHigh, for: 10m.
Cause:      §9.8's consumer-down row: pause web-bff, order, unpause it, again until the lag has lasted `for:`.
First step: the runbook's arrival-or-service reads, Translated: the consume-rate query, the lag query, a running check.
Restore:    unpause web-bff if a round left it paused, then wait for the late deliveries to leave the 10m window.
"""

from __future__ import annotations

import time

import harness

RUNBOOK = "queue-backlog.md"
ALERT = "DeliveryLagHigh"
CONSUMER = "web-bff"
LOG_SERVICE = "Web.Bff"
# The rule is `> 2` seconds, `for: 10m`. Late deliveries must run for the whole `for:` and a minute over, or the
# last evaluation before they leave the window is the only one short of it.
FOR_SECONDS = 600
DEADLINE = harness.Deadline(signal_seconds=0, for_seconds=FOR_SECONDS)
SUSTAIN_SECONDS = FOR_SECONDS + 120
# A message waits this long on the paused consumer's queue: past the 2s target with room for the p95's bucket.
HOLD_SECONDS = 20
# Between rounds, so each lands in its own export and the window holds many late deliveries and no early one.
GAP_SECONDS = 20
# The queries read what was exported, an export interval late, and a margin.
QUERY_WAIT_SECONDS = 180

# The runbook's queries, verbatim apart from their layout.
CONSUME_RATE = (
    'sum by (service_name) (rate(messaging_masstransit_consume_ea_total[10m])) - '
    '(sum by (service_name) (rate(messaging_masstransit_consume_errors_ea_total[10m])) or '
    '0 * sum by (service_name) (rate(messaging_masstransit_consume_ea_total[10m])))')
LAG_P95 = ('histogram_quantile(0.95, sum by (service_name, le) (rate(messaging_delivery_lag_seconds_bucket[10m])))')


def cause(world: harness.World) -> None:
    # A pause and not the row's `stop`: a restarted consumer exports as a new series, and `rate` counts nothing
    # for a series' first sample, which is where its late deliveries land, so the p95 would never read them.
    started = time.monotonic()
    rounds = 0
    while time.monotonic() - started < SUSTAIN_SECONDS:
        world.compose.pause(CONSUMER)
        order = world.orders.place()
        time.sleep(HOLD_SECONDS)
        world.compose.unpause(CONSUMER)
        rounds += 1
        world.say(f"round {rounds}: order {order} waited {HOLD_SECONDS}s on a paused {CONSUMER}")
        time.sleep(GAP_SECONDS)


def first_step(world: harness.World) -> tuple[bool, str]:
    # kubectl's replica count has no Compose form but whether the consumer runs at all: the lookalike.
    if not world.compose.running(CONSUMER):
        return False, f"{CONSUMER} is not running, which is the runbook's lookalike and not this cause"
    found: dict[str, list] = {}

    def reads() -> tuple[bool, str]:
        for key, query in (("rate", CONSUME_RATE), ("lag", LAG_P95)):
            found[key] = [row for row in world.alerts.query(query) if row["metric"].get("service_name") == LOG_SERVICE]
        counts = ", ".join(f"{key} {len(rows)}" for key, rows in found.items())
        return all(found.values()), f"series for {LOG_SERVICE}: {counts}"

    try:
        harness.wait_until(reads, QUERY_WAIT_SECONDS, "the runbook's two queries reading the consumer")
    except harness.Timeout as error:
        return False, str(error)
    rate, lag = found["rate"], found["lag"]
    if float(lag[0]["value"][1]) <= 2:
        return False, f"the runbook's lag query does not read past 2s for {LOG_SERVICE}: {lag}"
    return True, f"{LOG_SERVICE} finishes {float(rate[0]['value'][1]):.3f} messages/s, p95 lag {float(lag[0]['value'][1]):.1f}s"


def restore(world: harness.World) -> None:
    world.compose.unpause(CONSUMER)


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
