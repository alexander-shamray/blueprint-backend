# ADR-072 — A person replays dead letters as a broker account of its own

**Decision.** A message on an `_error` or `_skipped` queue is moved by
`tools/dead-letters`, run by a person, authenticated as `dead-letter-operator`:
tagged `management`, with no `configure`, `read` on no name but one ending
`_error` or `_skipped`, and `write` on the endpoint exchanges and their
dead-letter exchanges and on no contract, framework, delay or default
exchange. It is declared in
`deploy/compose/rabbitmq/definitions.json` with an empty password hash, so it
exists with that grant on every broker built from the file and nothing logs in
as it until an operator sets a password for an incident. `check_permissions.py`
holds the grant to the code in `check_operator`; `tools/dead-letters/README.md`
owns the grant's detail and the tool's contract.
**Why.** Both runbooks ended at a person moving messages by hand, and every
shape that step could take borrowed a principal that should not be lent: a
service's account carries that service's `write` and its identity into an
incident, and `guest` is the account
[ADR-036](ADR-036-the-broker-has-a-per-service-identity.md) removed. An
account of its own is the only one whose grant can be argued from what a
replay does. `management` is the lowest tag the Management API admits, a read
on a live queue would consume that endpoint's work, and leaving out the
contract exchanges and `amq.default` keeps the account from forging an event
to every subscriber or reaching any queue by name.
**Consequences.** The residual is ADR-036's own threat, accepted rather than
closed: `write` on every receive endpoint is the power to put a business command
on `ordering-commands` and have it mapped as system-initiated
([§9.4](../09-messaging.md)). A replay is exactly that act, so no narrower grant
replays; what bounds it is that only a person uses the account, with a password
set for the incident and cleared after, under one audit line per message, and
that no service ever holds it. A pattern cannot tell a queue from an exchange,
so the same grant also lets the account purge a dead-letter queue, unbind it
from its exchange so later faults are dropped unseen, and bind a dead-letter
exchange to its live queue so faults loop; the tool does none of these, and they
are accepted on the same terms. Provisioning it on a deployed broker is the
vault's obligation on ADR-036's terms, stated and not checked here. The
Management API has no move, so an executed run takes each message before it
publishes it, and a run that dies between the two leaves that message only in
the record file the tool requires; a take whose answer is lost may leave it in
neither, which the run reports rather than hides.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
