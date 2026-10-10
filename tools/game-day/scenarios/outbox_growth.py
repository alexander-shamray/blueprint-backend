"""docs/runbooks/outbox-growth.md. Alert: OutboxGrowth, over 1000 pending and rising, `for: 10m`.
Cause:      Forced through SQL: `stop rabbitmq`, Broker rows planted and topped up; the organic route is a long outage.
First step: the runbook's queries as written, and OutboxBrokerLaneStalled asserted firing beside it.
Restore:    stop the top-up, delete the planted rows by type, start rabbitmq, wait for both alerts to resolve.
"""

from __future__ import annotations

import threading

import harness

RUNBOOK = "outbox-growth.md"
ALERT = "OutboxGrowth"
COMPANION = "OutboxBrokerLaneStalled"
FORCED_THROUGH_SQL = True
FOR_SECONDS = 600
# The count crosses 1000 as soon as the first batch is exported; the deadline is the `for:` and the three intervals.
DEADLINE = harness.Deadline(signal_seconds=0, for_seconds=FOR_SECONDS)
SERVICE = "Ordering.Api"
MESSAGE_TYPE = "GameDay.Synthetic.Growth"
FIRST_BATCH = 1100
TOP_UP_ROWS = 20
TOP_UP_SECONDS = 30

# Set-based, so a batch is one statement; the id carries the batch's first number so no two batches collide.
_PLANT = """
WITH n AS (
    SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) + {offset} AS i
    FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b)
INSERT INTO ordering.OutboxMessages
    (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, Attempts, LastError)
SELECT CAST('6a3e0001-0000-4000-8000-' + RIGHT('000000000000' + CAST(i AS varchar(12)), 12) AS uniqueidentifier),
       CAST('6a3e0001-0000-4000-8000-' + RIGHT('000000000000' + CAST(i AS varchar(12)), 12) AS uniqueidentifier),
       N'{message_type}', N'{{}}', 'Broker', SYSDATETIMEOFFSET(), 0, N'planted by tools/game-day'
FROM n;
"""

# The runbook's queries, verbatim apart from their layout.
_AGE = "max by (service_name, lane) (outbox_oldest_age_seconds)"
_COUNT = "max by (service_name, lane) (outbox_pending_count)"
_DERIV = "deriv(sum by (service_name) (max by (service_name, lane) (outbox_pending_count))[10m:])"
_PURGE = """
SELECT
    Pending   = SUM(CASE WHEN ProcessedAt IS NULL THEN 1 ELSE 0 END),
    Processed = SUM(CASE WHEN ProcessedAt IS NOT NULL THEN 1 ELSE 0 END),
    Oldest    = MIN(OccurredAt)
FROM ordering.OutboxMessages;
"""
_REMOVE = f"DELETE FROM ordering.OutboxMessages WHERE MessageType = N'{MESSAGE_TYPE}';"


class _TopUp:
    """Keeps the backlog rising, on a thread, so the runner's wait for the alert is timed from the first batch."""

    def __init__(self) -> None:
        self.halt = threading.Event()
        self.failure: str | None = None
        self.thread: threading.Thread | None = None
        self.planted = 0

    def plant(self, world: harness.World, rows: int) -> None:
        sql = _PLANT.format(rows=rows, offset=self.planted, message_type=MESSAGE_TYPE)
        world.compose.exec_sql(sql)
        self.planted += rows

    def run(self, world: harness.World) -> None:
        while not self.halt.wait(TOP_UP_SECONDS):
            try:
                self.plant(world, TOP_UP_ROWS)
            except harness.GameDayError as error:
                self.failure = str(error)
                return


_top_up = _TopUp()


def _value(rows: list[dict], lane: str) -> float | None:
    for row in rows:
        if row["metric"].get("lane") == lane and row["metric"].get("service_name") == SERVICE:
            return float(row["value"][1])
    return None


def cause(world: harness.World) -> None:
    global _top_up
    _top_up = _TopUp()
    world.compose.stop("rabbitmq")
    _top_up.plant(world, FIRST_BATCH)
    world.say(f"rabbitmq stopped; planted {FIRST_BATCH} Broker rows, and {TOP_UP_ROWS} more every {TOP_UP_SECONDS}s")
    _top_up.thread = threading.Thread(target=_top_up.run, args=(world,), name="game-day-top-up", daemon=True)
    _top_up.thread.start()


def first_step(world: harness.World) -> tuple[bool, str]:
    if _top_up.failure:
        return False, f"the top-up stopped, so the backlog was not rising: {_top_up.failure}"
    age = _value(world.alerts.query(_AGE), "Broker")
    count = _value(world.alerts.query(_COUNT), "Broker")
    slope = [float(row["value"][1]) for row in world.alerts.query(_DERIV)
             if row["metric"].get("service_name") == SERVICE]
    if count is None or age is None or not slope:
        return False, f"the runbook's queries read nothing for {SERVICE}: age {age}, count {count}, deriv {slope}"
    if count <= 1000 or slope[0] <= 0:
        return False, f"the queries do not read the alert's condition: {count:.0f} pending, deriv {slope[0]:.3f}"
    if world.alerts.state(COMPANION) != "firing":
        return False, f"{COMPANION} is not firing beside it, though the oldest Broker row is {age:.0f}s old"
    purge = [line for line in world.compose.exec_sql(_PURGE).splitlines() if line.split("|")[0].strip().isdigit()]
    verdict = ("the age gauge is high too, so the runbook's own first check sends the reader to outbox-broker.md"
               if age > 120 else "the age gauge is low, so this is growth and not a stall")
    return True, (f"{count:.0f} Broker rows pending, deriv {slope[0]:.2f}/s, oldest {age:.0f}s; {verdict}; "
                  f"table {purge[:1]}")


def restore(world: harness.World) -> None:
    _top_up.halt.set()
    if _top_up.thread is not None:
        _top_up.thread.join(60)
    # Before the broker returns, so the dispatcher never publishes a synthetic message to a consumer.
    world.compose.exec_sql(_REMOVE)
    world.compose.start("rabbitmq")


def settled(world: harness.World) -> tuple[bool, str]:
    states = {name: world.alerts.state(name) for name in (ALERT, COMPANION)}
    return all(state == "inactive" for state in states.values()), ", ".join(f"{k} is {v}" for k, v in states.items())
