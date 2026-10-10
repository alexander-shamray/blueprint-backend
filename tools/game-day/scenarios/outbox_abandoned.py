"""docs/runbooks/outbox-abandoned.md — a broker-lane row past §9.4's attempt cap.

Alert:      OutboxAbandonedRows (max by (service_name, lane) (outbox_abandoned_count) > 0, no `for:`).
Cause:      Forced through SQL: one row is inserted into ordering.OutboxMessages on the Broker lane with Attempts =
            10, so the dispatcher's claim (`Attempts < @MaxAttempts`) skips it for ever and the gauge counts it.
            The organic route is a stopped broker for about 64 minutes, which is ten attempts on the dispatcher's
            2^min(Attempts, 8) × 5-second ladder. The row is the condition the rule reads, not the rule's history.
First step: the runbook's "Read the rows before touching them" query, run as written through sqlcmd. It passes
            when the result holds the planted row, found by its MessageId, on the Broker lane at Attempts 10.
Restore:    delete the planted row, by its MessageId and nothing wider, then wait for the alert to resolve. The
            runbook says never to delete rows to clear a backlog; this one is the harness's own and never was an
            event.
"""

from __future__ import annotations

import harness

RUNBOOK = "outbox-abandoned.md"
ALERT = "OutboxAbandonedRows"
FORCED_THROUGH_SQL = True
# The gauge reads above zero as soon as it is published.
DEADLINE = harness.Deadline(signal_seconds=0)
MESSAGE_ID = "6a3e0000-0000-4000-8000-0000000000dd"

_PLANT = f"""
INSERT INTO ordering.OutboxMessages
    (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, Attempts, LastError)
VALUES
    ('{MESSAGE_ID}', '{MESSAGE_ID}', N'GameDay.Synthetic', N'{{}}', 'Broker', SYSDATETIMEOFFSET(), 10,
     N'planted by tools/game-day');
"""

# The runbook's query, verbatim apart from its layout.
_STEP = """
SELECT Id, MessageId, CorrelationId, MessageType, Lane, Attempts, OccurredAt, LastError = LEFT(LastError, 2000)
FROM ordering.OutboxMessages
WHERE ProcessedAt IS NULL AND Attempts >= 10
ORDER BY OccurredAt;
"""

_REMOVE = f"DELETE FROM ordering.OutboxMessages WHERE MessageId = '{MESSAGE_ID}';"


def cause(world: harness.World) -> None:
    world.compose.exec_sql(_PLANT)
    world.say(f"planted {MESSAGE_ID} at the attempt ceiling")


def first_step(world: harness.World) -> tuple[bool, str]:
    rows = [line for line in world.compose.exec_sql(_STEP).splitlines() if MESSAGE_ID in line.lower()]
    if not rows:
        return False, "the runbook's query did not return the abandoned row"
    if "|Broker|" not in rows[0] or "|10|" not in rows[0]:
        return False, f"the row is not Broker at Attempts 10: {rows[0][:200]}"
    return True, rows[0][:200]


def restore(world: harness.World) -> None:
    world.compose.exec_sql(_REMOVE)


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
