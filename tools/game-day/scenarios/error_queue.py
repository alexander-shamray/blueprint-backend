"""docs/runbooks/error-queue.md. Alert: ErrorQueueDepth, any message on an `_error` queue, `for: 1m`.
Cause:      a CancelOrder whose reason no mapper knows, sent to ordering-commands: it faults with no retry (§9.8).
First step: the alert's `queue` label, the tool's `list` and `inspect`; Translated: Compose publishes 15672 already.
Restore:    the runbook's Discard with a record, `discard --message-id --execute`, then the password cleared.
"""

from __future__ import annotations

import uuid

import harness

RUNBOOK = "error-queue.md"
ALERT = "ErrorQueueDepth"
ENDPOINT = "ordering-commands"
QUEUE = f"{ENDPOINT}_error"
DEADLINE = harness.Deadline(signal_seconds=0, for_seconds=60)
MESSAGE_TYPE = "urn:message:Common.Contracts.Ordering.V1:CancelOrder"
# Not a CancellationReasons code, which CancelOrderMapper refuses with the exception the endpoint does not retry.
POISON_REASON = "game_day_poison"
# The runbook's "What you want off the result", as the tool names them.
FIELDS = ("message_id", "correlation_id", "fault_message")

_sent: list[str] = []


def cause(world: harness.World) -> None:
    _sent.clear()
    world.broker.open()
    _sent.append(world.broker.publish(ENDPOINT, MESSAGE_TYPE, {"orderId": str(uuid.uuid4()), "reason": POISON_REASON}))
    world.say(f"a CancelOrder with reason {POISON_REASON!r} sent to {ENDPOINT} as {harness.OPERATOR}: {_sent[0]}")


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
        return False, f"the tool's inspect exited {code} without the poison message: {inspected}"
    missing = [name for name in FIELDS if not ours[0].get(name)]
    if missing:
        return False, f"inspect gives the message without the runbook's {', '.join(missing)}"
    return True, (f"{QUEUE} holds {depth[QUEUE]}; {ours[0]['message_type']} faulted with "
                  f"{ours[0]['fault_exception_type']}: {ours[0]['fault_message']}")


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
