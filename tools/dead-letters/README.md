# The dead-letter tool

[`docs/runbooks/error-queue.md`](../../docs/runbooks/error-queue.md) and
[`docs/runbooks/skipped-queue.md`](../../docs/runbooks/skipped-queue.md) own
**when** a message on an `_error` or `_skipped` queue is replayed, discarded
or left alone. This directory owns **how**: a stdlib Python CLI over
RabbitMQ's Management API, the broker account it runs as, and the JSON it
prints for anything that drives it.

```bash
export DEAD_LETTERS_CREDENTIALS="$HOME/.rabbit.curl"   # the runbooks' curl config
py -3.12 tools/dead-letters/dead_letters.py list
py -3.12 tools/dead-letters/dead_letters.py inspect ordering-commands_error --limit 5
py -3.12 tools/dead-letters/dead_letters.py replay ordering-commands_error --message-id <id>
py -3.12 tools/dead-letters/dead_letters.py replay ordering-commands_error --message-id <id> \
    --execute --record incident.jsonl --audit-log audit.jsonl
py -3.12 tools/dead-letters/dead_letters.py discard ordering-commands_error --all \
    --execute --record incident.jsonl --audit-log audit.jsonl
```

| Command | What it does |
|---|---|
| `list` | every queue ending `_error` or `_skipped`, its endpoint and its depth |
| `inspect QUEUE` | reads up to `--limit` messages and puts them back: ids, `MT-Fault-*`, the body's `messageType`, the body |
| `replay QUEUE` | moves the named messages, or `--all`, back to the endpoint the queue name gives |
| `discard QUEUE` | removes the named messages, or `--all`, once each is in the record file |

**It reads only queues ending `_error` or `_skipped`.** Any other name is
refused before a request is made, so the tool cannot drain an endpoint's live
work by a slip of the queue name. The endpoint is the queue name minus its
suffix, which is the mapping both runbooks use.

## A dry run is the default

`replay` and `discard` change nothing without `--execute`. They read the queue
the way `inspect` does and print what they would do — `would-replay`,
`would-discard`, or `would-refuse` with the reason — and then the line that
says nothing left the queue. `--execute` also needs `--record FILE`, and is
refused without it before any request is made.

**A dry run is not invisible to the broker.** The Management API's `get` with
`ackmode=ack_requeue_true` reads a message and requeues it, and a requeued
message carries the `redelivered` flag from then on. On a classic queue — and
no endpoint here configures any other kind — it keeps its place. Nothing else
about it changes.

## What `--execute` does, one message at a time

The Management API has no move. The tool takes each message with
`ackmode=ack_requeue_false`, which removes it from the queue in the same call,
appends it whole — properties and base64 body — to the `--record` file and
syncs that file to disk, and only then publishes it:

- **a replay** to the endpoint's own exchange, the fanout exchange MassTransit
  binds to the endpoint's queue and that a `queue:` send targets;
- **a discard** nowhere, because the record is where it went;
- **a message the run did not select** back to the queue it came from, through
  that queue's own exchange, at the tail.

**Three windows remain, and all are around the take.** A run that dies
between the take and the publish has removed a message from the broker and
left it in the record file, and nowhere else; republish it from the record by
hand. That window is why `--record` is required rather than offered, and why a
discard's record is the one
[`error-queue.md`](../../docs/runbooks/error-queue.md) asks for before
anything is purged. The other is a take whose answer never arrives — a
timeout or a reset after the request was sent — which may have removed a
message the tool never saw: the run stops and reports it as `failed`, saying
the message may be in neither the queue nor the record, and only the
broker's own statistics can then say whether one left. The third is a record
that cannot be written after the take: the message is returned, and if the
return fails too it is in neither place, so the run writes it whole to
stderr on a line beginning `unrecorded`, and that line is the last copy.

A run takes at most as many messages as its first read saw, which `--limit`
bounds (100 by default), and stops early once every named id is handled, or
when a message it returned comes round again.

A replay is refused, and the message returned, when MassTransit's
`MT-Fault-InputAddress` names an endpoint other than the one the queue name
gives. A replay the broker routes nowhere, or refuses, is returned to the
dead-letter queue and stops the run; one whose answer is lost may have
landed, so it is not returned, and the run stops with the record holding it.
The `list` figures are the Management
API's statistics, which lag the queue by a few seconds.

## What a replay preserves

**The message id, every header and the body's bytes.** The publish carries
the `properties` the `get` returned — `message_id`, `correlation_id`, the
`MT-*` headers, content type and delivery mode — and the payload base64 both
ways, so not a byte of it is re-encoded. That is what makes a replay safe to
repeat: [§9.5](../../docs/backend-architecture/09-messaging.md)'s inbox keys
on the `MessageId` and
[ADR-057](../../docs/backend-architecture/adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md)'s
fingerprint is of the command as serialised, so a duplicate is the same
message to both, and is refused as one.

**Headers travel through the API's JSON**, which is the limit the runbooks'
shovel does not have: strings, numbers, booleans, lists and tables
round-trip, and a value JSON cannot carry — an AMQP timestamp or a byte array
in a header — comes back as the API rendered it. MassTransit's own headers are
strings and numbers.

## The account: `dead-letter-operator`

[ADR-072](../../docs/backend-architecture/adr/ADR-072-a-person-replays-dead-letters-as-a-broker-account-of-its-own.md)
decides that a person replays as an account of its own; this section owns the
grant it holds.

| | Grant | Why |
|---|---|---|
| Tag | `management` | the lowest tag the HTTP API admits. Not `policymaker`, which creates shovels and policies, and not `administrator`, which creates users |
| `configure` | none | the tool declares nothing, so it may delete nothing |
| `read` | `_(error\|skipped)$` | a read on a live queue is a consume of that endpoint's work, so the dead letters and nothing else |
| `write` | `^[a-z][a-z0-9-]*(_error\|_skipped)?$` | a replay lands on an endpoint's exchange and a returned message on its dead-letter exchange |

**What the `write` pattern leaves out is the point of it.** No contract
exchange — those carry capitals, dots and a colon — so the account cannot
forge an event to every subscriber; no `MassTransit:` exchange; no `_delay`
exchange, which is ADR-021's scheduler; and not `amq.default`, the default
exchange, whose write reaches every queue by name. Measured on
`rabbitmq:4.1-management-alpine` with this grant: a publish through
`amq.default`, a `get` on `ordering-commands`, a queue declare and a read of
`/api/users` were each refused, and a replay preserved id, headers and body.

**The residual is stated rather than closed.** Write on every receive endpoint
is the power to put a business command on `ordering-commands`, which is the
threat
[ADR-036](../../docs/backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md)
removed from the services. A replay is exactly that act, so the grant cannot
be narrower than it; what bounds it is that a person uses it, under the audit
line below, and no service ever does. Because a pattern cannot tell a queue
from an exchange, the grant also admits three acts the tool never makes —
purging a dead-letter queue, unbinding it from its exchange so later faults
are dropped unseen, and binding a dead-letter exchange to its live queue so
faults loop — and
[ADR-072](../../docs/backend-architecture/adr/ADR-072-a-person-replays-dead-letters-as-a-broker-account-of-its-own.md)
accepts them on the same terms.

**It ships with no password.** `deploy/compose/rabbitmq/definitions.json`
declares it with an empty `password_hash`, so it exists with this grant on the
Compose broker and in every test broker, and nothing can log in as it — the
Compose README's "no login ships" stays true. Give it one for an incident on
the local stack, and take it away after:

```bash
docker compose exec rabbitmq rabbitmqctl change_password dead-letter-operator '<password>'
docker compose exec rabbitmq rabbitmqctl clear_password dead-letter-operator
```

On a deployed broker it is provisioned from the vault, on the terms ADR-036
sets for every account: an obligation this repository states and does not
check. **`check_permissions.py` holds the grant to the code**, in
`check_operator`: the dead letters and endpoints of every receive endpoint the
services declare are covered; no `configure`, no read on a live or `_delay`
queue, and no write on a contract, framework, private, `_delay` or default
exchange is; and the password hash is empty.

## The credential

**Never on the command line**, for the reason the runbooks give: `argv` is
visible to every user on the box. In order:

1. `--credentials FILE`, or `DEAD_LETTERS_CREDENTIALS`, naming the runbooks'
   mode-0600 curl config — one line, `user = "NAME:PASSWORD"`;
2. `DEAD_LETTERS_PASSWORD`, with `DEAD_LETTERS_USER` defaulting to
   `dead-letter-operator`.

`--url`, or `DEAD_LETTERS_URL`, is the Management API, by default
`http://localhost:15672` — the runbooks' `kubectl port-forward`. `--vhost` is
`/` unless given.

## The audit line

**One JSON line per action**, on stderr and, with `--audit-log FILE`, appended
there too. It is one line; here it is wrapped to be read:

```json
{"ts": "2026-10-07T09:30:00+00:00", "operator": "oncall",
 "broker_user": "dead-letter-operator", "vhost": "/",
 "queue": "ordering-commands_error", "message_id": "…", "action": "replayed",
 "destination": "ordering-commands", "reason": null}
```

`operator` is the login running the tool and `broker_user` the account it
reached the broker as. `action` is `replayed`, `discarded`, `returned` (a
message the run did not select), `refused` or `failed`. `inspect` and a dry
run write none, because they move nothing.

## The JSON contract

**`--json` prints one document on stdout, and its shape is a contract**:
`blueprint-admin`'s error-queue workbench shells out to this tool and reads it.
`schema` is `1`; a change that renames or removes a field or a value raises it,
and one that only adds a field does not.

| Command | Fields |
|---|---|
| `list` | `schema`, `command`, `vhost`, `queues[]` of `name`, `kind` (`error` or `skipped`), `endpoint`, `messages` |
| `inspect` | `schema`, `command`, `vhost`, `queue`, `kind`, `endpoint`, `messages[]` of `message_id`, `correlation_id`, `message_type`, `fault_message`, `fault_exception_type`, `input_address`, `reason`, `redelivered`, `properties`, `body` (text, or `null` when not UTF-8), `body_base64` |
| `replay`, `discard` | `schema`, `command`, `vhost`, `queue`, `kind`, `endpoint`, `dry_run`, `actions[]` of `message_id`, `action`, `destination`, `reason`, and `not_found[]` |
| any, refused | `schema`, `command`, `error` |

An `action` is one of the audit line's, or `would-replay`, `would-discard` or
`would-refuse` in a dry run.

| Exit | Meaning |
|---|---|
| 0 | done, or a dry run |
| 1 | a message was refused or failed, or a named id was not on the queue |
| 2 | refused before anything moved: the queue name, a missing flag, the credential, or the broker's answer |

## Tests

```bash
cd tools/dead-letters && py -3.12 -m unittest
```

The suite runs against an in-memory double of the four Management API calls
the tool makes, and CI's `dead-letters` job runs it.
