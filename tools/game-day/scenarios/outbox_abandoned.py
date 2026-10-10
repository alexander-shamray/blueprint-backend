"""docs/runbooks/outbox-abandoned.md (§9.4's cap). Alert: OutboxAbandonedRows, no `for:`.
Cause:      Forced through SQL: a Broker row planted at Attempts 10; the organic route is 64 minutes of a stopped broker.
First step: the runbook's query as written, which must return the planted row by its MessageId.
Restore:    delete that row by its MessageId and nothing wider, then wait for the alert to resolve.
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
