"""docs/runbooks/queue-backlog.md. Alerts: DeliveryLagHigh, then QueueBacklogGrowing as NEXT, each `for: 10m`.
Cause:      web-bff paused 20s a round past the `for:`; then stopped, its queue sent 1100 messages and 20 every 30s.
First step: Translated: the consume-rate and lag queries, a running check; then the depth, `queue` and consumer reads.
Restore:    unpause web-bff and wait out the 10m window; then stop the top-up, purge, and start web-bff again.
"""

from __future__ import annotations

import threading
import time
from types import SimpleNamespace

import harness

RUNBOOK = "queue-backlog.md"
ALERT = "DeliveryLagHigh"
CONSUMER = "web-bff"
LOG_SERVICE = "Web.Bff"
# The rule is `> 2` seconds, `for: 10m`. Late deliveries must run for the whole `for:` and a minute over, or the
# last evaluation before they leave the window is the only one short of it.
FOR_SECONDS = 600
DEADLINE = harness.Deadline(signal_seconds=0, for_seconds=FOR_SECONDS)
# The late deliveries leave the rule's 10-minute `rate` window ten minutes after the last.
SETTLE = harness.Deadline(signal_seconds=600)
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
DEPTH = 'sum by (queue) (rabbitmq_queue_messages{queue!~".+_(error|skipped)"})'


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


BACKLOG_ALERT = "QueueBacklogGrowing"
QUEUE = "bff-order-events"
# A type nothing binds, and none is ever delivered: the restore purges them all before the consumer is back.
BACKLOG_TYPE = "urn:message:GameDay:Backlog"
# The rule is `> 1000` and a positive `deriv` over 10 minutes, `for: 10m`; the first batch crosses the depth at once.
FIRST_BATCH = 1100
TOP_UP_MESSAGES = 20
TOP_UP_SECONDS = 30


class _TopUp:
    """Keeps the queue rising, on a thread, so the runner's wait for the alert is timed from the first batch."""

    def __init__(self) -> None:
        self.halt = threading.Event()
        self.failure: str | None = None
        self.thread: threading.Thread | None = None
        self.sent = 0

    def send(self, world: harness.World, count: int) -> None:
        for _ in range(count):
            world.broker.publish(QUEUE, BACKLOG_TYPE, {})
            self.sent += 1

    def run(self, world: harness.World) -> None:
        while not self.halt.wait(TOP_UP_SECONDS):
            try:
                self.send(world, TOP_UP_MESSAGES)
            except harness.GameDayError as error:
                self.failure = str(error)
                return


_top_up = _TopUp()


def _backlog_cause(world: harness.World) -> None:
    global _top_up
    _top_up = _TopUp()
    world.compose.stop(CONSUMER)
    world.broker.open()
    _top_up.send(world, FIRST_BATCH)
    world.say(f"{CONSUMER} stopped; {FIRST_BATCH} messages sent to {QUEUE}, and {TOP_UP_MESSAGES} more every "
              f"{TOP_UP_SECONDS}s")
    _top_up.thread = threading.Thread(target=_top_up.run, args=(world,), name="game-day-top-up", daemon=True)
    _top_up.thread.start()


def _backlog_first_step(world: harness.World) -> tuple[bool, str]:
    if _top_up.failure:
        return False, f"the top-up stopped, so the queue was not rising: {_top_up.failure}"
    queues = sorted(labels.get("queue", "?") for labels in world.alerts.labels(BACKLOG_ALERT))
    if QUEUE not in queues:
        return False, f"the alert's queue label does not name {QUEUE}: {queues}"
    depth = [float(row["value"][1]) for row in world.alerts.query(DEPTH) if row["metric"].get("queue") == QUEUE]
    if not depth or depth[0] <= 1000:
        return False, f"the runbook's depth query does not read {QUEUE} past 1000: {depth}"
    # The lookalike, which the runbook says to rule out before scaling: an endpoint with no consumer at all.
    consumers = world.broker.queues().get(QUEUE, (0, -1))[1]
    running = world.compose.running(CONSUMER)
    rate = [row for row in world.alerts.query(CONSUME_RATE) if row["metric"].get("service_name") == LOG_SERVICE]
    if consumers != 0 or running:
        return False, f"{QUEUE} has {consumers} consumers and {CONSUMER} running is {running}; the lookalike is missed"
    finished = f"{float(rate[0]['value'][1]):.3f}/s" if rate else "no data"
    return True, (f"{QUEUE} at {depth[0]:.0f}; it has no consumer and {CONSUMER} is not running, which is the "
                  f"runbook's lookalike: scaling would add replicas that are not there. {LOG_SERVICE} finishes "
                  f"{finished}")


def _backlog_restore(world: harness.World) -> None:
    _top_up.halt.set()
    if _top_up.thread is not None:
        _top_up.thread.join(60)
    try:
        # Before the consumer starts, so web-bff never reads a synthetic message and parks it in _skipped.
        world.broker.purge(QUEUE)
        world.compose.start(CONSUMER)
    finally:
        world.broker.close()


def _backlog_settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(BACKLOG_ALERT)
    depth, consumers = world.broker.queues().get(QUEUE, (0, 0))
    return (state == "inactive" and consumers > 0,
            f"{BACKLOG_ALERT} is {state}, {QUEUE} holds {depth} with {consumers} consumers")


NEXT = SimpleNamespace(
    RUNBOOK=RUNBOOK, ALERT=BACKLOG_ALERT, DEADLINE=DEADLINE, cause=_backlog_cause, first_step=_backlog_first_step,
    restore=_backlog_restore, settled=_backlog_settled)
