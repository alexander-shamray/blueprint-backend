"""docs/runbooks/contact-refused.md (the grant row). Alert: ContactReadRefused, no `for:`.
Cause:      take view-users from notifications-worker's service account, drop the stored contacts, restart, order.
First step: the ContactRefused read, Translated from kubectl to Loki (§13.4); its exception must name the lost role.
Restore:    give the role back and read the account back, restart the worker, then wait out the rule's 30m window.
"""

from __future__ import annotations

import harness

RUNBOOK = "contact-refused.md"
ALERT = "ContactReadRefused"
# The order has to reach Notifications and a send tick has to claim its row before the counter moves.
DEADLINE = harness.Deadline(signal_seconds=120)
# `increase(...[30m]) > 0` stays true until the last refusal leaves the window, so the cause's sum is no bound.
SETTLE = harness.Deadline(signal_seconds=30 * 60)
GRANT = harness.Grant("service-account-notifications-worker", "realm-management", "view-users")
WORKER = "notifications-worker"
LOG_SERVICE = "Notifications.Worker"
# The runbook's row for a token that lost the role, which is the exception's own wording; the hosts log an
# exception as structured metadata and not in the line, so the line is matched apart from it.
CAUSE_MESSAGE = "realm-management role(s) where ADR-052 names"
CONTACT_REFUSED = "The contact read for notification .* was refused over this host's credential"

# A fresh stored contact is sent from without asking Keycloak (ContactOptions.Freshness), so the read the
# cause refuses would never be made for a customer the worker has already read.
_FORGET_CONTACTS = "DELETE FROM notifications.ContactRecords;"


def cause(world: harness.World) -> None:
    world.compose.exec_sql(_FORGET_CONTACTS, "Notifications")
    GRANT.take(world.realm)
    # The worker keeps the token it fetched until it expires, so only a restart makes the next pass use one
    # issued after the revoke.
    world.compose.restart(WORKER)
    world.say(f"{GRANT.role} taken from {GRANT.account}; placed order {world.orders.place()}")


def first_step(world: harness.World) -> tuple[bool, str]:
    since = world.since_cause()
    refused = world.logs.search(LOG_SERVICE, CONTACT_REFUSED, since)
    if not refused:
        return False, "Loki has no `The contact read for notification … was refused` line from the worker"
    named = world.logs.search(LOG_SERVICE, CONTACT_REFUSED, since, exception=harness.regex_literal(CAUSE_MESSAGE))
    if not named:
        return False, f"no ContactRefused exception says `{CAUSE_MESSAGE}`; the first line says: {refused[0].strip()[:200]}"
    stdout = world.compose.logs(WORKER, "15m").strip()
    note = "" if stdout else " (the container's stdout is empty, so the runbook's kubectl read finds nothing)"
    return True, named[0].strip()[:200] + f" with exception `{CAUSE_MESSAGE}`" + note


def restore(world: harness.World) -> None:
    GRANT.give_back(world.realm)
    world.compose.restart(WORKER)


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
