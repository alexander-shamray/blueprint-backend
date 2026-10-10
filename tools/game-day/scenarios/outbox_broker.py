"""docs/runbooks/outbox-broker.md (§9.8's broker-down row). Alert: OutboxBrokerLaneStalled, no `for:`.
Cause:      `stop rabbitmq`, then place an order; its OrderPlaced row waits on the broker lane.
First step: the log read, Translated from kubectl to Loki, as the hosts log through OpenTelemetry alone (§13.4).
Restore:    start rabbitmq, then wait for the alert to resolve.
"""

from __future__ import annotations

import harness

RUNBOOK = "outbox-broker.md"
ALERT = "OutboxBrokerLaneStalled"
# The age must read above 120s; the rule has no `for:`.
DEADLINE = harness.Deadline(signal_seconds=120)
SERVICE = "ordering-api"
LOG_SERVICE = "Ordering.Api"


def cause(world: harness.World) -> None:
    world.compose.stop("rabbitmq")
    world.say(f"rabbitmq stopped; placed order {world.orders.place()}")


def first_step(world: harness.World) -> tuple[bool, str]:
    claim = world.logs.search(LOG_SERVICE, "Outbox claim failed", world.since_cause())
    message = world.logs.search(
        LOG_SERVICE, r"Outbox message .* on lane Broker failed, attempt [0-9]+ of [0-9]+", world.since_cause())
    stdout = world.compose.logs(SERVICE, "10m").strip()
    note = "" if stdout else " (the container's stdout is empty, so the runbook's kubectl read finds nothing)"
    if claim:
        return False, f"the log says the dispatcher cannot reach SQL Server: {claim[0].strip()}"
    if not message:
        # With the broker stopped MassTransit's publish retries inside the call, so the dispatcher never
        # reaches its own failure log; the runbook reads that silence as a dispatcher that is not running.
        unreachable = world.logs.search(LOG_SERVICE, "Broker unreachable", world.since_cause())
        return False, (f"no `Outbox message … on lane Broker failed` line, which the runbook reads as a dispatcher "
                       f"that is not running; Loki holds {len(unreachable)} `Broker unreachable` lines from "
                       f"MassTransit instead")
    return True, message[0].strip() + note


def restore(world: harness.World) -> None:
    world.compose.start("rabbitmq")


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    return state == "inactive", f"{ALERT} is {state}"
