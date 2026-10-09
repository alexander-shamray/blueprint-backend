# Personal data erasure — the choreography §11.7 draws

Design spec, frozen at write time. No PR number: Appendix C is closed and
says a gap the plan left is a pull request whose body says so, so this is
dated and named for its subject, like the service specs beside it. Where
this document and the blueprint disagree, the blueprint wins.

**What is already decided, and where.**
[§11.7](../../backend-architecture/11-identity-authorization.md) draws the
choreography: a `PersonalDataDeleteRequestedV1` broadcast, a consumer in each
service that holds personal data deleting or anonymising what it owns, a
`PersonalDataDeleteCompletedV1` back, and a Privacy service that tracks the
completions and escalates on silence. It names the four rules each consumer
keeps — delete or anonymise per record, write an audit record, be idempotent,
report completion — and says the log store is answered by a lifetime, not a
delete. [`docs/personal-data.md`](../../personal-data.md) is the list of
holdings the rule produces, with the erasure path each owner has decided.
[ADR-035](../../backend-architecture/adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
keeps personal data out of event payloads,
[ADR-052](../../backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
sets Shipping's and Notifications' contact rows and their deletion, and
[ADR-053](../../backend-architecture/adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)
makes the notification log evidence that is anonymised and not deleted.
[§9.2](../../backend-architecture/09-messaging.md) orders a release that adds
a consumer ahead of the release that starts publishing what it consumes.

This document does not restate any of that. It records what those chapters
leave open and the decisions taken on each, so the PRs below can be argued
against something written down.

## 1. What the blueprint leaves open, and the answers

**Whether Privacy is a service or an operator command.** **A seventh
service, `Privacy`**, with its own database. The choreography needs state
that outlives any one process: the request, which responders have answered,
and when the completion service level was missed. A command that keeps that
state has a database, a retry rule and an escalation, and is a service under
another name without §4.2's boundaries around it. **Rejected**: an operator
CLI that publishes the request and reads a report, because silence is the
one outcome choreography cannot tell from success (§11.7), and a tool that
runs once cannot notice it.

**What the service is.** Two hosts, as the services that take a request and
also work in the background are shaped: `Privacy.Api` takes the request and
reads its status, and `Privacy.Worker` holds the consumer of
`PersonalDataDeleteCompletedV1` and the sweep that finds an overdue request.
The aggregate is `ErasureRequest`: a `RequestId`, the subject, the
expected-responder set frozen at the moment it was raised, the completions
received, a status of `Open`, `Closed` or `Overdue`, and the times that
matter. It is the only aggregate. **Rejected**: one host doing both, because
the completion consumer must scale and fail apart from an HTTP surface a
person can reach.

**Who may raise a request.** A staff principal, by the policy §11 already
gives staff writes. The API has no Gateway route in this design: how an
operator reaches an internal host is the deployment's, and a public route to
erasure belongs to an adopter's legal reading (ADR-062), not to the
reference implementation. A request raised for a subject with an open
request returns that request and publishes nothing.

**What a subject is.** The Keycloak `sub` the platform already calls
`CustomerId` everywhere. A staff user's id, which keys Catalog's idempotency
markers, is outside this design and stays *lifetime only*.

**Who is in the responder set.** A configuration value,
`PrivacyOptions.Responders`, read once when a request is raised and stored
on it, so a later change to the list does not move a request already open.
Its default is the five holders `docs/personal-data.md` lists: Ordering,
Payments, Shipping, Notifications and the BFF. Catalog and Inventory are
absent because that file records them as checked and holding none. A
completion from a name outside the stored set is recorded, flagged and never
counted. A holder missing from the set is the failure the section warns
about, so §4's journey test fails when a store holds the subject and its
owner is not a responder.

**The contracts.** Both are in `Common.Contracts` under `Privacy.V1`, in one
file as `ShipmentEvents.cs` is. `PersonalDataDeleteRequestedV1` carries
`RequestId` and `SubjectId` beside the three primitives of
[§9.1](../../backend-architecture/09-messaging.md);
`PersonalDataDeleteCompletedV1` carries `RequestId`, a `Responder` and a
`Count`. The subject's id in the first is personal data in a payload, which
ADR-035 forbids as a rule, and it is the one exception: a responder cannot
find what to erase without it. The consequence is stated where the exception
is made and not worked round: the broker, the request's outbox row and any
parked copy hold the id until they age out, and §6 lists them with the
other things this choreography cannot reach. `Responder` is a closed
vocabulary in §9.2's sense, which is why a new responder is a consumer
release ahead of the set that names it.

**Completion is published by many services, and the contract has one
owner.** §9.1 puts a contract in its publisher's namespace, and this is the
one message with six publishers. It lives with Privacy, which consumes it,
because the vocabulary is Privacy's: its set decides what a valid
`Responder` is.

**The audit record.** Each responder writes a row in its own database:
`RequestId`, a `SubjectHash`, a count and a time. `SubjectHash` is the
SHA-256 of the request id and the subject id taken together, so two requests
for one person produce two unrelated values and nobody holding only the row
can recover the subject, while a holder of both ids can check it. The row
carries no personal data, as §11.7 requires, and is kept for as long as the
responder's own records of processing are; the option that bounds it is the
owner's and `RetentionMapRule` requires one.

**What Privacy keeps of the subject.** Its request row holds the subject's
id, which makes the row personal data and a new holding in
`docs/personal-data.md`. When the request closes, the id is replaced by the
same `SubjectHash` and the row is evidence from then on. An overdue request
keeps the id, because the reissue below needs it.

**Overdue and reissue.** `PrivacyOptions.CompletionSlo` bounds an open
request. A request past it becomes `Overdue`, the worker raises a metric
naming the missing responders (§13.6 owns where an alert lives), and nothing
closes it by itself. An operator reissues it: the same `RequestId`, a fresh
`MessageId`. Every consumer is idempotent on the request and not on the
message, so a reissue erases again what a rebuild or a late write put back
and succeeds silently where there is nothing to do.

## 2. What each holder does

The erasure path of every holding is `docs/personal-data.md`'s, and where
that file reads *None decided* this design decides, because a holder that
lists a subject's id and has no step is the gap the journey test would find.
The decisions below are proposals for the owning PR to amend, and the file
is amended in the same PR.

| Holder | Step | Where it differs from the file |
|---|---|---|
| Ordering | Anonymise `Orders` as §11.7 says; **delete** the subject's `OrderSummaries` rows | The file decides nothing for the summaries. They are a read model of the buyer's list; the order beside them is the record that survives |
| Payments | **Anonymise** `PaymentOrders.CustomerId` and keep the money | The file decides nothing. A payment is a financial record, so it takes the order's path |
| Shipping | **Delete** `DeliveryAddresses` for the subject, as ADR-052 | None |
| Notifications | **Delete** `ContactRecords`, the subject's waiting `NotificationLog` rows and their `OrderRecords`; **anonymise** every ended `NotificationLog` row | `OrderRecords` was undecided. It exists so a pending notice can find its order, and no notice remains to need it |
| The BFF | **Delete** the subject's `bff.Orders` rows | The file decides nothing. The projection is rebuilt from the publishers' outboxes, which is the residual §6 names |
| Privacy | Replace the subject on a closed request, as above | New |

Each step is one transaction in the holder's own database that also writes
its audit row and stages its completion through the outbox (§9.4), so the
completion is published if and only if the erasure committed. A consumer that
finds nothing writes the row with a count of zero and publishes, because a
holder that stays silent when it has nothing to do is indistinguishable from
a holder that did not run.

The consumers are registered on each service's receive endpoint through the
existing messaging extension and sit behind the inbox (§9.5). No holder
reaches another's store, and none reads the subject's data to decide: the id
is the whole input.

## 3. Places the blueprint and the tree move

| Place | Moves how |
|---|---|
| [§11.7](../../backend-architecture/11-identity-authorization.md) | Names Payments and the BFF in the diagram's responders, drops "appears in no bounded-context table" once the service exists, and states the one contract exception above |
| [§3.2](../../backend-architecture/03-bounded-contexts.md), §4.1 | A seventh context and its tree |
| [§9.1](../../backend-architecture/09-messaging.md) | The contract with many publishers, in one place |
| [`docs/personal-data.md`](../../personal-data.md) | Every *None decided* above becomes a decision; Privacy's request and each audit table are rows |
| [§15](../../backend-architecture/15-cicd-deployment.md), `deploy/` | Privacy's chart and compose entry, and its broker account |
| A new ADR | Privacy is a service, the subject's id in the request is the one exception to ADR-035, and the responder set is stored on the request |
| [Appendix C](../../backend-architecture/appendix-c-delivery-plan.md) | Not edited; it is closed, and each PR body says it fills a gap |

The broker-permissions gate is the first thing to say a consumer is missing
from an account, because this is the first event every service consumes. It
derives each account from the code and needs no list; the PR that adds the
consumers is the PR that proves it did.

## 4. The PR sequence

§9.2 puts a consumer ahead of the producer, in two releases, and that fixes
the order: nothing publishes `PersonalDataDeleteRequestedV1` until every
holder consumes it.

| PR | Carries | Why here |
|---|---|---|
| 1 | The two contracts, the ADR, the chapter and `personal-data.md` amendments | Nothing runs; every later PR cites it |
| 2 | The consumers in Ordering and Payments | Consumer first |
| 3 | The consumers in Shipping and Notifications | Consumer first |
| 4 | The consumer in the BFF | Consumer first |
| 5 | The Privacy service, its two hosts, chart and broker account | The producer: the first PR that publishes |
| 6 | The journey test and the responder-coverage check | Needs every store |

The journey requests erasure for a customer with a delivered order, runs the
whole stack, and then **scans every column of every table in every service's
database for the subject's id and the address's text**, passing only when
neither appears anywhere but an audit row's hash. A scan, because a
per-table assertion covers the tables its author remembered, and a store
nobody listed is exactly what is being looked for. Its mutation case drops a
responder from the set and requires the run to fail. Each consumer's own
tests cover the second erasure, the zero-row case and the rollback that
publishes nothing.

Plans follow as `docs/superpowers/plans/`, one per PR and each written ahead
of its PR.

## 5. Configuration

`PrivacyOptions` carries `Responders`, `CompletionSlo` and the retention of
a closed request's evidence row. Each holder's audit retention is an option
in its own jurisdiction options, named by `docs/personal-data.md`. Nothing
here is a constant: the periods are the adopter's reading of the law
(ADR-053, #427).

## 6. What the choreography cannot reach

Each is answered, and none by a delete.

| Residual | Answer |
|---|---|
| Keycloak's user record | Not a participant (ADR-052). Deleting the user is the deployment's act, ordered after the choreography closes, because the contact rows are read from it |
| The log and trace stores | Lifetime only (§11.7, §13.4); the logs' lifetime is #453 |
| The broker, and messages parked in `_error` or `_skipped` | Lifetime only; no chapter bounds it. The request carries the subject's id, so it is itself one of these |
| Abandoned outbox rows | Lifetime only, and the reason a purge was never designed (§11.7) |
| A BFF rebuild inside the outbox window | It re-reads payloads that name the subject. The reissue answers it; Privacy keeps the id on an overdue request only, so a closed one cannot be reissued. **This is a gap**, filed against ADR-051's owner and not closed by this design |
| Idempotency markers and claim keys | Lifetime only, as the file says |
| The carrier, the mail relay, the payment provider | Processors under the adopter's agreement, outside the platform |
| Backups | The deployment's |

## 7. What this design deliberately does not do

It does not trigger erasure from Keycloak's user-deleted event: a deletion
the identity provider performs is not the data subject's request, and the
legal reading belongs to the adopter. It does not erase a staff user. It
adds no Gateway route. It does not make the broker forget. It does not
choose between erasure and a retention window for any holder beyond the
paths §2 states; where a window applies, §11.7 has ruled: anonymise, keep
the financial record.
