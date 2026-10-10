"""docs/runbooks/projection-lag.md (the local lane). Alert: OutboxLocalLaneStalled, no `for:`.
Cause:      Forced through SQL: OrderSummaries is renamed so the projection throws; the organic route is a migration.
First step: the log read, Translated from kubectl to Loki (§13.4), then the runbook's SQL as written.
Restore:    rename the table back, safe twice, then wait for the alert to resolve.
"""

from __future__ import annotations

import harness

RUNBOOK = "projection-lag.md"
ALERT = "OutboxLocalLaneStalled"
FORCED_THROUGH_SQL = True
DEADLINE = harness.Deadline(signal_seconds=30)
SERVICE = "ordering-api"
LOG_SERVICE = "Ordering.Api"
TABLE = "ordering.OrderSummaries"
RENAMED = "OrderSummariesGameDay"

# The runbook's query, verbatim apart from its layout.
FIRST_STEP_SQL = """
SELECT TOP 10 Id, MessageId, MessageType, Attempts, LockedUntil, LastError = LEFT(LastError, 2000)
FROM ordering.OutboxMessages
WHERE ProcessedAt IS NULL AND Lane = 'Local'
ORDER BY OccurredAt;
"""

_RENAME = f"EXEC sp_rename '{TABLE}', '{RENAMED}';"
_UNRENAME = (f"IF OBJECT_ID('ordering.{RENAMED}') IS NOT NULL AND OBJECT_ID('{TABLE}') IS NULL "
             f"EXEC sp_rename 'ordering.{RENAMED}', 'OrderSummaries';")


def cause(world: harness.World) -> None:
    world.compose.exec_sql(_RENAME)
    world.say(f"{TABLE} renamed; placed order {world.orders.place()}")


def first_step(world: harness.World) -> tuple[bool, str]:
    logged = world.logs.search(LOG_SERVICE, r"Outbox message .* on lane Local failed", world.since_cause())
    if not logged:
        return False, "Loki has no `Outbox message … failed` line"
    rows = world.compose.exec_sql(FIRST_STEP_SQL)
    if "OrderSummaries" not in rows:
        return False, f"the runbook's query did not show an error naming the table: {rows[:300]}"
    stdout = world.compose.logs(SERVICE, "15m").strip()
    note = "" if stdout else " (the container's stdout is empty, so the runbook's kubectl read finds nothing)"
    return True, logged[0].strip() + note


def restore(world: harness.World) -> None:
    world.compose.exec_sql(_UNRENAME)


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
