"""docs/runbooks/unattributed-order.md. Alert: UnattributedOrders, no `for:`.
Cause:      Forced through SQL: a payment fact with no owner; the organic route is an Ordering event that never comes.
First step: the runbook's find query and its question 1 as written, then its delete, which is the restore.
Restore:    the runbook's guarded delete of that OrderId, then wait for the alert to resolve.
"""

from __future__ import annotations

import harness

RUNBOOK = "unattributed-order.md"
ALERT = "UnattributedOrders"
FORCED_THROUGH_SQL = True
# The row is not backdated: the gauge is its age, and the rule fires past 900s of it.
DEADLINE = harness.Deadline(signal_seconds=900)
ORDER_ID = "6a3e0000-0000-4000-8000-0000000000ff"

_PLANT = f"""
INSERT INTO bff.Orders (OrderId, CustomerId, PaymentCurrency, AuthorisedAt, AuthorisedAmount, FirstSeenAt, AsOf)
VALUES ('{ORDER_ID}', NULL, N'EUR', SYSDATETIMEOFFSET(), 1.00, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
"""

# The runbook's queries, verbatim apart from their layout and the order id it leaves as @OrderId.
_FIND = """
SELECT OrderId, FirstSeenAt, AsOf, AuthorisedAt, RefundedAt, DispatchedAt, DeliveredAt, TrackingNumber
FROM bff.Orders
WHERE CustomerId IS NULL
ORDER BY FirstSeenAt;
"""

_ASK_ORDERING = f"""
SELECT Id, CustomerId, Status
FROM ordering.Orders
WHERE Id = '{ORDER_ID}';
"""

_DELETE = f"""
DELETE FROM bff.Orders
WHERE OrderId = '{ORDER_ID}'
    AND CustomerId IS NULL;
"""


def cause(world: harness.World) -> None:
    world.compose.exec_sql(_PLANT, "Bff")
    world.say(f"planted unowned order {ORDER_ID} with a payment fact and no Ordering event")


def first_step(world: harness.World) -> tuple[bool, str]:
    rows = [line for line in world.compose.exec_sql(_FIND, "Bff").splitlines() if ORDER_ID in line.lower()]
    if not rows:
        return False, "the runbook's find query did not list the unowned row"
    if ORDER_ID in world.compose.exec_sql(_ASK_ORDERING, "Ordering").lower():
        return False, "Ordering has the order, so the runbook would not send the reader to delete the row"
    # Question 1 found no order, which is the runbook's branch ending in the delete.
    world.compose.exec_sql(_DELETE, "Bff")
    left = [line for line in world.compose.exec_sql(_FIND, "Bff").splitlines() if ORDER_ID in line.lower()]
    if left:
        return False, "the runbook's delete left the row"
    return True, rows[0][:200]


def restore(world: harness.World) -> None:
    world.compose.exec_sql(_DELETE, "Bff")


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
