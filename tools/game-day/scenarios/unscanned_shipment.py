"""docs/runbooks/unscanned-shipment.md. Alert: UnscannedShipments, no `for:`.
Cause:      Forced through SQL: a Booked row, four days old, no scan, not due for a poll; the organic route is 3 days.
First step: the runbook's two queries as written, which must list the planted row and show no scan since.
Restore:    delete that row by its Id and nothing wider, then wait for the alert to resolve.
"""

from __future__ import annotations

import harness

RUNBOOK = "unscanned-shipment.md"
ALERT = "UnscannedShipments"
FORCED_THROUGH_SQL = True
# A count of rows already past their age, so it reads above zero once ShipmentStats' 5s cache lets it be asked.
DEADLINE = harness.Deadline(signal_seconds=5)
SHIPMENT_ID = "6a3e0000-0000-4000-8000-0000000000ee"
DATABASE = "Shipping"

# CreatedAt is past ShipmentStats.FirstScanAge (3 days). NextPollAt is a day ahead: the gauge needs one, and the
# tracking worker would otherwise poll the row and the carrier simulator would answer with a scan.
_PLANT = f"""
INSERT INTO shipping.Shipments
    (Id, OrderId, Status, CarrierReference, TrackingNumber, Attempts, NextAttemptAt, NextPollAt, CreatedAt)
VALUES
    ('{SHIPMENT_ID}', '{SHIPMENT_ID}', N'Booked', N'crr_GAMEDAY', N'TRK-GAMEDAY', 1, SYSDATETIMEOFFSET(),
     DATEADD(day, 1, SYSDATETIMEOFFSET()), DATEADD(day, -4, SYSDATETIMEOFFSET()));
"""

# The runbook's queries, verbatim apart from their layout.
_LAST_SCAN = """
SELECT LastScan = MAX(RecordedAt)
FROM shipping.TrackingEvents;
"""

_UNSCANNED = """
SELECT Id, OrderId, CarrierReference, CreatedAt
FROM shipping.Shipments s
WHERE s.Status = 'Booked'
    AND (s.CancellationRequestedAt IS NULL
        OR s.CancellationRefusedAt IS NOT NULL)
    AND NOT EXISTS (
        SELECT 1 FROM shipping.TrackingEvents e WHERE e.ShipmentId = s.Id)
ORDER BY CreatedAt;
"""

_REMOVE = f"DELETE FROM shipping.Shipments WHERE Id = '{SHIPMENT_ID}';"


def cause(world: harness.World) -> None:
    world.compose.exec_sql(_PLANT, DATABASE)
    world.say(f"planted Booked shipment {SHIPMENT_ID}, four days old and never scanned")


def first_step(world: harness.World) -> tuple[bool, str]:
    # sqlcmd prints the column's name, its underline and a row count around the one value.
    last_scan = " ".join(line for line in world.compose.exec_sql(_LAST_SCAN, DATABASE).splitlines()
                         if line and not line.startswith(("LastScan", "--", "(")))
    rows = [line for line in world.compose.exec_sql(_UNSCANNED, DATABASE).splitlines()
            if SHIPMENT_ID in line.lower()]
    if not rows:
        return False, "the runbook's second query did not list the unscanned booking"
    return True, f"{rows[0][:200]}; the first query read LastScan {last_scan[:40]}"


def restore(world: harness.World) -> None:
    world.compose.exec_sql(_REMOVE, DATABASE)


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
