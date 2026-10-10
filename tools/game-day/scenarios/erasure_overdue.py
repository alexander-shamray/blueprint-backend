"""docs/runbooks/erasure-overdue.md. Alert: ErasureRequestsOverdue, no `for:`.
Cause:      Forced through SQL: an Open request due a day ago, marked by the sweep; the organic route is a month.
First step: the runbook's first query as written, which must list the planted request as Overdue.
Restore:    delete that request by its id and nothing wider, then wait for the alert to resolve.
"""

from __future__ import annotations

import harness

RUNBOOK = "erasure-overdue.md"
ALERT = "ErasureRequestsOverdue"
FORCED_THROUGH_SQL = True
# OverdueSweepService.Interval is a minute, and the gauge reads the table on every collection.
DEADLINE = harness.Deadline(signal_seconds=70)
REQUEST_ID = "6a3e0000-0000-4000-8000-0000000000ef"
DATABASE = "Privacy"

# The subject is a made-up id that names no one, and the set is the five holders the local stack runs with.
_PLANT = f"""
INSERT INTO privacy.ErasureRequests
    (RequestId, SubjectId, Status, RaisedAt, DueAt, RespondersCsv, Reissues)
VALUES
    ('{REQUEST_ID}', '{REQUEST_ID}', N'Open', DATEADD(day, -31, SYSDATETIMEOFFSET()),
     DATEADD(day, -1, SYSDATETIMEOFFSET()), N'ordering,payments,shipping,notifications,bff', 0);
"""

# The runbook's query, verbatim apart from its layout.
_OVERDUE = """
SELECT r.RequestId, r.Status, r.RaisedAt, r.DueAt, r.Reissues, r.RespondersCsv
FROM privacy.ErasureRequests r
WHERE r.Status = 'Overdue'
ORDER BY r.DueAt;
"""

_REMOVE = f"""
DELETE FROM privacy.ErasureCompletions WHERE RequestId = '{REQUEST_ID}';
DELETE FROM privacy.ErasureRequests WHERE RequestId = '{REQUEST_ID}';
"""


def cause(world: harness.World) -> None:
    world.compose.exec_sql(_PLANT, DATABASE)
    world.say(f"planted Open erasure request {REQUEST_ID}, due a day ago and answered by no holder")


def first_step(world: harness.World) -> tuple[bool, str]:
    rows = [line for line in world.compose.exec_sql(_OVERDUE, DATABASE).splitlines()
            if REQUEST_ID in line.lower()]
    if not rows:
        return False, "the runbook's first query did not list the overdue request"
    return True, rows[0][:200]


def restore(world: harness.World) -> None:
    world.compose.exec_sql(_REMOVE, DATABASE)


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
