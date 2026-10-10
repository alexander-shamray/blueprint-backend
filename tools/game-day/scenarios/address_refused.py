"""docs/runbooks/address-refused.md (the grant row). Alert: AddressReadRefused, no `for:`.
Cause:      take orders:delivery-address from shipping-worker's service account in Keycloak, restart the worker, order.
First step: the PassFailed read, Translated from kubectl to Loki (§13.4); its exception must name the lost role.
Restore:    give the role back and read the account back, restart the worker, then wait out the rule's 30m window.
"""

from __future__ import annotations

import harness

RUNBOOK = "address-refused.md"
ALERT = "AddressReadRefused"
# The order has to reach Shipping and a fulfilment tick has to claim it before the counter moves.
DEADLINE = harness.Deadline(signal_seconds=120)
# `increase(...[30m]) > 0` stays true until the last refusal leaves the window, so the cause's sum is no bound.
SETTLE = harness.Deadline(signal_seconds=30 * 60)
GRANT = harness.Grant("service-account-shipping-worker", "commerce-api", "orders:delivery-address")
WORKER = "shipping-worker"
LOG_SERVICE = "Shipping.Worker"
# The runbook's row for a token that lost the role, which is the exception's own wording; the hosts log an
# exception as structured metadata and not in the line, so the line is matched apart from it.
CAUSE_MESSAGE = "permission(s) where ADR-052 names exactly one"
PASS_FAILED = "Fulfilment pass for shipment .* failed; the row backs off"


def cause(world: harness.World) -> None:
    GRANT.take(world.realm)
    # The worker keeps the token it fetched until it expires, so only a restart makes the next pass use one
    # issued after the revoke.
    world.compose.restart(WORKER)
    world.say(f"{GRANT.role} taken from {GRANT.account}; placed order {world.orders.place()}")


def first_step(world: harness.World) -> tuple[bool, str]:
    since = world.since_cause()
    failed = world.logs.search(LOG_SERVICE, PASS_FAILED, since)
    if not failed:
        return False, "Loki has no `Fulfilment pass for shipment … failed` line from the worker"
    named = world.logs.search(LOG_SERVICE, PASS_FAILED, since, exception=harness.regex_literal(CAUSE_MESSAGE))
    if not named:
        return False, f"no PassFailed exception says `{CAUSE_MESSAGE}`; the first line says: {failed[0].strip()[:200]}"
    stdout = world.compose.logs(WORKER, "15m").strip()
    note = "" if stdout else " (the container's stdout is empty, so the runbook's kubectl read finds nothing)"
    return True, named[0].strip()[:200] + f" with exception `{CAUSE_MESSAGE}`" + note


def restore(world: harness.World) -> None:
    GRANT.give_back(world.realm)
    world.compose.restart(WORKER)


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
