"""docs/runbooks/skipped-queue.md. Alert: SkippedQueueDepth, any message on a `_skipped` queue, `for: 1m`.
Cause:      a message of a type nothing binds sent to ordering-stock-events' exchange as dead-letter-operator, the
            runbook's "addressed here and does not belong here"; the organic route is a producer ahead of its consumer.
First step: the tool's `list` and `inspect` as dead-letter-operator, Translated: Compose publishes 15672, so the
            runbook's `kubectl port-forward` has nothing to do; the type read out of the body, where the runbook says.
Restore:    the runbook's Discard with a record, the tool's `discard --message-id --execute`, then the password cleared.
"""

from __future__ import annotations

import harness

RUNBOOK = "skipped-queue.md"
ALERT = "SkippedQueueDepth"
ENDPOINT = "ordering-stock-events"
QUEUE = f"{ENDPOINT}_skipped"
DEADLINE = harness.Deadline(signal_seconds=0, for_seconds=60)
# A type no consumer on any endpoint declares, so the endpoint can only park it.
MESSAGE_TYPE = "urn:message:GameDay:Unrouted"

_sent: list[str] = []


def cause(world: harness.World) -> None:
    _sent.clear()
    world.broker.open()
    _sent.append(world.broker.publish(ENDPOINT, MESSAGE_TYPE, {}))
    world.say(f"a {MESSAGE_TYPE} sent to {ENDPOINT} as {harness.OPERATOR}: {_sent[0]}")


def first_step(world: harness.World) -> tuple[bool, str]:
    queues = sorted(labels.get("queue", "?") for labels in world.alerts.labels(ALERT))
    if QUEUE not in queues:
        return False, f"the alert's queue label does not name {QUEUE}: {queues}"
    code, listed = world.broker.tool("list")
    depth = {row["name"]: row["messages"] for row in listed.get("queues", [])}
    if code or not depth.get(QUEUE):
        return False, f"the tool's list exited {code} without {QUEUE} holding a message: {listed}"
    code, inspected = world.broker.tool("inspect", QUEUE, "--limit", "5")
    ours = [message for message in inspected.get("messages", []) if message["message_id"] in _sent]
    if code or not ours:
        return False, f"the tool's inspect exited {code} without the parked message: {inspected}"
    # The whole diagnosis is the type, compared against what the endpoint binds.
    if MESSAGE_TYPE not in (ours[0].get("message_type") or []):
        return False, f"inspect does not give the body's messageType: {ours[0].get('message_type')}"
    return True, f"{QUEUE} holds {depth[QUEUE]}; type {MESSAGE_TYPE}, MT-Reason {ours[0].get('reason')}"


def restore(world: harness.World) -> None:
    # Opened again, because a cause that raised before its open leaves the tool no password to run with.
    world.broker.open()
    try:
        world.broker.discard(QUEUE, list(_sent))
    finally:
        world.broker.close()


def settled(world: harness.World) -> tuple[bool, str]:
    state = world.alerts.state(ALERT)
    depth = world.broker.queues().get(QUEUE, (0, 0))[0]
    return state == "inactive" and depth == 0, f"{ALERT} is {state}, {QUEUE} holds {depth}"
