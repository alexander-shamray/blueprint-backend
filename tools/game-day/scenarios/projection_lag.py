"""docs/runbooks/projection-lag.md — Ordering's local lane stalls while ordering-api stays up.

Alert:      OutboxLocalLaneStalled (outbox_oldest_age_seconds{lane="Local"} > 30, no `for:`).
Cause:      ordering-api has to stay up, because that process emits the gauge, so the broker and the host are
            left alone. Forced through SQL: `ordering.OrderSummaries` is renamed, so OrderSummaryProjection's
            MERGE throws "Invalid object name" on every attempt, which is the runbook's schema-drift cause. The
            organic route is a migration that renames a column under a running host, which a Compose run does
            not produce without a second build; the order placed afterwards is the real trigger.
First step: the runbook's "Find the throwing handler" log read. Translated: `kubectl -n <ns> logs deploy/ordering
            --since=15m | grep "Outbox message .* failed"` reads a container's stdout, and the hosts log through
            OpenTelemetry alone (§13.4), so the step as written finds nothing; the Compose equivalent reads the
            same lines from Loki under the same pattern. Then its SQL, run as written through sqlcmd: the step passes
            when the row it selects is on the Local lane and its LastError names the renamed table.
Restore:    rename the table back (a no-op when it is not renamed), then wait for the alert to resolve, which
            needs the failed rows to retry and succeed.
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
