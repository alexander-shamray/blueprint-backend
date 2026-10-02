# Notifications — the sixth service

Design spec, frozen at write time. No PR number: Appendix C is closed and
says a gap the plan left is a pull request whose body says so, so this is
dated and named for its subject, like the Shipping spec beside it. Where
this document and the blueprint disagree, the blueprint wins.

**What is already decided, and where.**
[§3.2](../../backend-architecture/03-bounded-contexts.md) gives
Notifications its row: it owns `NotificationLog`, publishes nothing, accepts
no command, and consumes seven events — `OrderPlaced`, `OrderConfirmed`,
`OrderCancelled`, `PaymentDeclined`, `PaymentRefunded`,
`ShipmentDispatched` and `ShipmentDelivered`, every one of which now has a
publisher. §3.1 calls it *Generic*, replaceable by an off-the-shelf product
without regret, and §3.2 its only *pure* consumer.
[§4.1](../../backend-architecture/04-solution-structure.md) gives it four
projects — Application, Infrastructure, Migrator, Worker — and no Domain,
and [§4.5](../../backend-architecture/04-solution-structure.md) says the
scaffold refuses the name until that second mode exists.
[ADR-052](../../backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
decides where the mailbox and the locale come from — Keycloak, read by a
worker under the `view-users` grant and kept in a table of the service's
own, with its five outcomes, its two freshness numbers and its give-up age.
[ADR-053](../../backend-architecture/adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)
decides that a language set, a time zone and every statutory window are
values the deployment is given, that a made-up jurisdiction proves it, that
a mail relay is a processor with a country, and what the record of a send
holds and never holds.
[ADR-049](../../backend-architecture/adr/ADR-049-a-cancellation-payments-has-recorded-declines-the-authorisation-that-follows.md)
decides that a customer who cancelled is never told their payment failed,
and that the deciding fact is Notifications' own record of the cancellation.
Shipping's spec already built the worker shape and the client-credentials
types in `Common.Infrastructure`, which this service takes, and Shipping's
own grant-checked token cache, whose shape this service copies over a
claim of its own.

This document does not restate any of that. It records what those chapters
leave open and the decisions taken on each, so the PRs below can be argued
against something written down.

## 1. What the blueprint leaves open, and the answers

**The channel.** **Email, and only email**, over SMTP through one
`IMailChannel` port. A second channel — SMS, push — is the same seam with a
second adapter, and is refused here because nothing in the platform holds
a telephone number or a device token: each would be a contact ADR-052 does
not cover and a new personal-data table, which is an ADR before it is a pull
request. "Build it to be thrown away" is honoured by keeping the port the
place a vendor would go — a hosted email API is one adapter behind it, and
nothing above the port knows the word SMTP.

**The SMTP package.** **MailKit**, which brings MimeKit: both MIT, both
maintained, and the de facto .NET client since `System.Net.Mail.SmtpClient`
was marked as not recommended for new development. MimeKit encodes a
non-ASCII subject and a non-ASCII display name itself (RFC 2047), and
refuses a header value containing CR or LF when it is built through its
types — which is the property section 9 leans on rather than reimplements.
Each is a pin, an Appendix B row and a licence-gate subject.

**The relay in Compose.** **Mailpit**, an SMTP sink with a web UI and an HTTP
API, MIT-licensed, pinned to a version and never `latest`. Its SMTP port and its
UI are published on `127.0.0.1` only: the UI because it shows every message to
anyone who can reach it, and the SMTP port so that §14.1's infrastructure-only
workflow, which runs the worker on the host, has a relay to reach. The container
is a unit in `deploy/compose/services/`, beside the worker it serves, and
carries no file of its own to mount. The tests start the same image under
Testcontainers and read what arrived through the HTTP API, so the sink a person
watches in Compose is the sink the suite asserts against. A log-only channel was
the other candidate for the tests, and is refused: it would prove the renderer
and nothing about the bytes a server receives, which is where the encoding and
header rules live.

**Where the outbound calls sit.** **In one worker, and in no consumer**,
by ADR-052 for the contact read and by the same argument for the send: a
consumer that called a relay would hold its endpoint's slot across a third
party's latency, and a dead relay would be met by every message on its own
ladder and end in `_error`, which §13.6 pages at one message for an outage
that is nobody's defect. So the seven consumers write rows and acknowledge,
and the send worker does everything that leaves the service. This retires
three things the earlier plan asked of the consumers — an `Ignore<>` list
for refused recipients, a kill switch on the endpoints, and a breaker whose
open state parks messages — because none of those faults can now reach a
consumer at all. A breaker remains, on the worker's channel, and section 4
says what its open state does.

**How a notification learns its customer.** Four of the seven events carry
no `CustomerId`: `PaymentDeclined`, `PaymentRefunded`, `ShipmentDispatched`
and `ShipmentDelivered` name an order and nothing else. The three Ordering
events do. So Notifications keeps **an order record** — an `OrderRecords`
row per order, written by the consumers of the three Ordering events and
holding the customer's id and whether, why and by whom the order was
cancelled — and a notification for any of the other four waits in its row
until the record exists. This is
[ADR-017](../../backend-architecture/adr/ADR-017-one-synchronous-hop.md)'s
own instrument — cross-context data arrives by event and is projected
locally — so it is no departure. **Rejected**: a `CustomerId` on four
contracts, which is four new versions under §9.2 for a value the platform
already delivers; and a read of Ordering, which is a second permission, a
second gRPC method and a third credential for a fact already on the bus.

**What a decline tells a customer.** ADR-049 forbids telling a customer who
cancelled that their payment failed, and names this service's own record of
`OrderCancelled` as the deciding fact. So a `PaymentDeclined` notification waits
for its order record to carry a cancellation — every decline has one, published
before or after it (§9.6) — and then sends if the cancellation's origin is
`workflow`, and is **suppressed**, a terminal outcome on its row, if it is
`user`. A cancellation with no origin — an older publisher, which
`OrderCancelled.Origin`'s remark allows — or with an origin outside
`CancelOrigins`' two, which a newer one could send, is read as `workflow` when
its reason is `payment_declined` or `payment_timeout` and as `user` otherwise,
and a test holds each case. `PaymentDeclined.Reason` is never read: ADR-049
forbids branching on it, and a provider's code is no message for a customer.

**Whether a cancellation says why.** It does, from a closed map of
`CancelReasons`' five codes to a phrase per language, and a code the map
does not know renders a generic phrase rather than failing — a publisher
adds a reason on its own schedule, and a consumer that faults on a new one
stops every cancellation notice until a deploy. That map branches on
`OrderCancelled.Reason`, which is a closed vocabulary Ordering owns; it is
not `PaymentDeclined.Reason`, which nothing may branch on.

**One message per event, seven templates.** A payment declined by the
provider produces two messages — the decline and the cancellation that
follows it — and that is kept rather than folded: §3.2's row is a
subscription per event, the decline says what happened and the
cancellation what it means for the order, and a rule that suppressed one by
reading the other would make each template's sending depend on a second
event's arrival order. The suppression above is the one exception, and
ADR-049 is what makes it one.

**The body's content type.** **`text/plain; charset=utf-8`, and nothing
else.** HTML mail is a second body to encode for, a second rendering to
test in every language, and a styling surface that a vendor replacing this
service would bring anyway; a Generic service built to be thrown away takes
the one format every client reads. A link is not rendered either: the
tracking number is, and section 8 says why no URL is.

**The template engine.** **None.** A template is a text file whose
placeholders are `{Name}` drawn from a closed set per template key;
rendering replaces each from a typed parameter set and nothing else. A
library that evaluates expressions in a template is a language somebody
can write into a file, and these files carry nothing that needs one. The
host refuses at start a template that names a placeholder its key's set
does not hold, and a key × version × language the deployment requires that
is missing (section 7).

**The languages shipped.** **English, Kazakh and Russian** — `en`, `kk`
and `ru` — because ADR-053 names the United Kingdom and Kazakhstan and the
second owes consumer information in two languages. A deployment's set is a
subset of what is shipped, validated at start; a third country with a
language not shipped is a template set and a values file, which is what
ADR-053 rule 2 promises and section 13's `ZZ` test proves.

**Whether the realm turns internationalisation on.** No. ADR-052 already
reads an absent locale as an answer, ADR-053 says such a customer is sent
the deployment's whole required set in one message, and turning the realm's
languages on is a decision about the sign-up screens, which are not this
service's. When a locale arrives whose primary subtag is in the
deployment's set — `ru`, `ru-RU` and `RU` all choose Russian — the message
is rendered in it alone; any other locale, or none, renders every language
in the set.

**Whether Notifications needs Redis.** No, as Shipping does not: its worker
claims rows under a lease in SQL, it caches nothing, and it has no HTTP
command for §8.5's keys. §2's sentence that names the services reaching no
Redis gains Notifications.

**Whether Notifications has an outbox.** No. It publishes nothing, so it
has no outbox table, no dispatcher, no mapper registry and no outbox
gauges. Section 2 says what the scaffold's mode strips and which gates are
told so, with the reason, rather than left to discover it.

## 2. Places the blueprint and the tree move

Each rides in the PR that makes it true; section 14 lists them by PR.

- **`tools/new-service` gains a pure-consumer mode**, `--pure-consumer`,
  which implies `--worker` and renders §4.1's four projects: no Domain
  project and no Domain suite, no domain-event collector, and — because a
  pure consumer publishes nothing — no outbox table, no dispatcher, no
  outbox publisher, no outbox gauges and no integration-event mapper. The
  inbox, the migrator, the health endpoint and the messaging registration
  stay. `Notifications` comes off the scaffold's refusal and onto
  `WORKER_ONLY_SERVICES`' stricter sibling: it renders under this mode or
  not at all. §4.5's sentence that the mode is owed moves with it, and
  anything hand-fixed after the render is a defect fixed in the scaffold in
  the same change.
- **Every gate that reads a service's shape is told about a service with no
  outbox**, with a reason the gate states: `check.py`'s outbox metrics, the
  architecture suites that assert a Domain project's rules, and whichever
  of the mapper and registration tests assumes a publisher. **Each is
  changed by selector, never by a list that names Notifications** — the
  rule is "a service with no Domain project" or "a service whose §3.2
  Publishes cell is empty", read from the tree or from §3.2's table, so the
  next pure consumer needs no edit. PR-1's plan finds each by reading what
  the gate looks at, and a test whose subject is the selector proves it.
- **What stripping costs outside the gates, and where it is paid.** The
  building blocks assume every service publishes, in two places, and each is
  answered in the service or made optional, never by a second building
  block edited:
  - `TransactionBehavior` requires an `IDomainEventDispatcher`, and the only
    one dispatches through a collector, a mapper and a publisher. The mode
    writes an internal `NoDomainEventDispatcher` into the service's
    Application project, and an architecture test fails the build if any
    type in the service raises a domain event, so the empty dispatcher can
    never be swallowing one. §7.5 gains the paragraph that says so.
  - `RetentionPurgeService` takes the outbox table unconditionally. Its
    outbox parameter becomes optional, so a service with no outbox purges
    its inbox and nothing else, and §9.5 gains the sentence.
  - The two Dockerfiles copy the Domain project's file, and the template's
    Compose unit carries comment blocks the comment gate fails on a new
    file. The mode patches the first; the second is cut in the template, so
    every future render passes, and a scaffold test judges a rendered unit
    whole.
- **The broker account is rendered by the mode, not hand-edited after it.**
  `check_permissions.py` requires every account to write
  `Common.Contracts:IIntegrationEvent`; that requirement moves to the
  services that publish, and a pure consumer's account is refused any
  contract write. Whether MassTransit's consumer topology needs the write at
  run time is measured, not assumed: PR-4's live broker-binding test is the
  measurement, and a failure there is the selector being wrong.
- **Coverage does not see the record's rules**, because §12.9's filter
  selects `*.Domain.dll` and section 5 puts the rules in Application. The
  filter is not widened to name a service; the Application suite drives
  every row of section 5's table, and a test pins the gap so it is a stated
  one.
- **§2's no-Redis sentence** names Notifications beside Payments and
  Shipping, in PR-1.
- **The realm gains `notifications-worker`**, a confidential client with a
  service account, `commerce-api` **not** among its client scopes, and on
  `realm-management` the `view-users` role with the two query roles it
  composes and nothing else — ADR-052's grant, and the third credentialed
  client after `web-bff` and `shipping-worker`. The asserted rows of
  ADR-052's table this client turns red — `RealmClientTests` and
  `RealmImportTests`' secret test — PR-3 takes; the chart rows go red with
  PR-6's chart (section 14).
- **§14.1 gains Mailpit** — the container, its two loopback ports in the
  endpoint table and the relay's local defaults — in PR-2. §14.2's Aspire
  sample runs no Notifications resource and declares nothing for one, so it
  does not move. **§12.7** gains the relay's row, the one third party it
  meets through a container rather than WireMock.Net, and **§9.7**'s list of
  third-party hops gains `MailHop` beside `ProviderHop` and `CarrierHop`.
- **§15.4's inventory gains the relay's keys and the jurisdiction's**, and
  §15.4's sentence listing the options types that earned one gains `Mail`
  and Notifications' `Jurisdiction` and `Delivery`, each in the PR that binds
  it.
- **Appendix B** gains MailKit, MimeKit and Polly.Core in PR-2. The
  breaker's pipeline names Polly.Core's types directly, and a project
  that names a package's types references it rather than leaning on a
  transitive path, so it takes a pin at the version every other graph
  already resolves, and a
  row of its own: its licence is BSD-3-Clause, which the existing
  `Microsoft.Extensions.Http.Resilience` row does not say.
- **§11.7's erasure diagram** is checked and not redrawn: it already draws
  Notifications anonymising `NotificationLog` rows, which is ADR-053 rule
  4's replacement of the customer's id. What it does not draw — the contact
  row and the order record deleted, the waiting work marked terminal — is
  owed with the extension, as Shipping's was, and section 6 names the path.

## 3. The PR sequence

Six, where the earlier plan said four: the scaffold's mode is a render, the
channel and the contact read are each an anti-corruption layer with a
dependency of its own, and the send worker is what joins them and is the
largest piece of judgement in the service. Each row names its
[change class](../../change-locality.md); the touch set is the PR body's.

| PR | Subject | Class |
|---|---|---|
| 1 | `feat(notifications): sixth service from the scaffold's pure-consumer mode` — the mode in `tools/new-service` and its suite; the gates told by selector; the render with `AddRedisConnections` stripped and §2's sentence amended; `NotificationLog` and its state rules in `Notifications.Application`, its configuration and the first migration; the Compose pair; `ci.yml`'s filter, outputs, matrix legs and the `images` job's `if:`; the broker account `notifications-svc` | A+D+E |
| 2 | `feat(notifications): the mail channel and the Compose relay` — `IMailChannel`, the MailKit adapter, `MailHop`'s numbers and the breaker, `MailOptions` with the STARTTLS refusal, the channel counter and its `AddMeter` line, the Mailpit unit, §14.1's, §12.7's and §9.7's amendments, §15.4's rows, Appendix B's three rows, the Kazakh-script subject through the sink. Nothing calls it yet | A+D+E |
| 3 | `feat(notifications): the notifications-worker client and the contact read` — the realm's client, `realm_check.py`'s predicate, `RealmClientTests` and `RealmImportTests`' secret test, `docs/secrets.md`'s rows; `IContactSource`, the Keycloak adapter with `ContactHop`, the grant-checked token cache over `realm-management`'s roles, `ContactRecords` and its migration, `ContactOptions`. Nothing calls it yet | A+D+E |
| 4 | `feat(notifications): the seven consumers, the order record and the templates` — `notifications-events`, the seven consumers writing intent rows, `OrderRecords` and its migration, the twenty-one templates, the renderer and its start-time checks, `NotificationsJurisdictionOptions`, §15.4's rows for it | A+D+E |
| 5 | `feat(notifications): the send worker` — the claim and its lease, the order-record wait, ADR-049's suppression, ADR-052's five outcomes over PR-3's source, the render, the send with its `Message-ID`, the intent and completion stamps, the give-up age under `DeliveryOptions`, the breaker's park, the retention pass, the waiting and overdue gauges, the order journey through the service's own queue | A+D+E |
| 6 | `feat(deploy): Notifications' chart, deploy target and canary` — `deploy/helm/notifications` with `service.enabled: false` and `redis.enabled: false`, the library chart's `mail`, `contactSource` and `delivery` capabilities, its `jurisdiction` block generalised to ADR-053 rule 1's three kinds of member with Shipping's chart listing its own, the client-credentials one, `_helpers.tpl`'s list of credentialed charts, the umbrella, the deploy descriptor `deploy/canary/deployables/notifications.json`, the dashboard's service variable, the shared runbook's Notifications half | D |

**Order.** 1 first; 2 and 3 in either order after it, and a second session
can take one while the first builds the other; 4 after 1 and 3, since its
retention window is refused below PR-3's stale ceiling; 5 needs 2, 3 and
4; then 6. PR-3's realm half touches no Notifications path and could move
earlier, and does not: a client minted before its reader exists is a secret
with no consumer, which `docs/secrets.md`'s rotation row would have to
describe as such.

**Why `NotificationLog` lands in PR-1 and nothing writes it until PR-4.**
Payments' PR-1 carried its record and Shipping's its shipment for the same
reason: the scaffold's proof is a service that migrates and starts, and a
render with no table of its own proves the template and not the service.

**PR-1 is the mode and the render in one pull request, with the render a
commit of its own.** `tools/new-service/README.md` says a new service's first
pull request is the scaffold's output alone and the scaffold is reviewed in
its own; §4.5 says the mode comes off the refusal with the pull request that
builds it, and Shipping's worker mode landed with Shipping's render for the
same reason. The README's purpose is that the render is reviewable as
generated rather than edited, and `--verify` over the render's commit proves
exactly that, so the pull request carries the mode's commits, then the
render alone, then the record — and the review reads the render through
`--verify` rather than line by line. A split would leave a mode merged with
no service proving it.

**Why CI joins PR-1 and Helm does not** is the Inventory spec's argument
unchanged: the pipeline gate refuses a service directory no filter matches,
and `smoke.sh` and the deploy workflow read their charts from the
descriptors under `deploy/canary/deployables/`, so a chart is owed the day
its descriptor is added and not before.

## 4. The send worker

**One worker, `SendWorker`, a `BackgroundService` in `FulfilmentWorker`'s
shape**, and the shape is Shipping's spec section 4 unchanged: the claim is
taken before the call under `UPDLOCK, READPAST, ROWLOCK` and stamped as a
lease longer than every call a pass can make; a failed row backs off on its
own `Attempts` and `NextAttemptAt`, apart from the lease; the loop catches
per pass and its filter asks the token, not the exception's type; a pass
fits the thirty-second drain. Two replicas overlapping claim a row once.

**A pass over one claimed row**, in this order, each step a branch the
tests drive:

1. **The order record.** Absent: the row backs off and waits — the event
   reached Notifications before its order's `OrderPlaced`, which §9.4 does
   not order. For a decline, present without a cancellation: the same.
2. **The suppression.** A decline whose record's cancellation is the
   customer's (section 1) is `Suppressed`; nothing further is asked.
3. **The contact**, by ADR-052's five outcomes over PR-3's source and
   `ContactOptions`. A refused credential backs off and is counted; "does
   not exist" is `Undeliverable: no_such_customer`.
4. **The render**, in the contact's locale when the deployment's set holds
   it, otherwise in every language of the set (section 7). The template's
   version is stamped on the row here, so the row names what was rendered,
   not what was current when the event arrived.
5. **The intent.** `SendStartedAt` is stamped and committed **before** the
   send. A row claimed with `SendStartedAt` already set is a send that may
   have reached the relay; it is sent again under the same `Message-ID`,
   **rendered from the version and languages already stamped** rather than
   the current ones, so one `Message-ID` never carries two texts, and
   counted on `notifications.mail.resent`, which is the record PLAN's
   durability posture asks for — the duplicate is on the row and on a
   counter, never invisible.
6. **The send**, under `MailHop` (below).
7. **The completion.** `Sent` and `CompletedAt`, committed.

**At-least-once, with the window stated.** A redelivered message is the
inbox's to drop, so a redelivery after the consumer's commit writes nothing
twice; and the worker, not the consumer, is what sends, so a redelivery
never reaches the relay at all. The window that remains is between the
relay's `250 OK` and step 7's commit: a crash there sends a second email
that nothing on this side can prevent. The message therefore carries a
**`Message-ID` derived from the row's event id and template key** —
`<{EventId:N}.{TemplateKey}@{domain of Mail:From}>`, the port taking the two
ids and the adapter adding the domain, since `Mail:From` is
configuration the Application layer never sees — so a receiver that
deduplicates on it can, and a test stages that crash on purpose and finds
two deliveries with one `Message-ID` and one `Sent` row.

**A send ends in one of three ways: accepted, refused, or not yet.**

| The relay answers | The port returns | Retried in the client | The row |
|---|---|---|---|
| `250` to the data | `Accepted` | — | `Sent` |
| a permanent `5xx` to the recipient — no such mailbox, refused | `Refused(RecipientRefused)` | — | `Undeliverable: recipient_refused`, terminal |
| nothing: the contact's mailbox does not parse, or carries CR or LF | `Refused(NotAMailbox)` | — | `Undeliverable: not_a_mailbox`, terminal |
| a `4xx` — to the data included, since it says the relay holds nothing — or a timeout or a refused connection **before the message was handed over**, which is before the send began: connecting, TLS, authentication | throws `MailUnavailableException`, cause `transient` | yes | backs off |
| nothing: `MailHop`'s breaker is open, so no connection is made | throws `MailUnavailableException`, cause `transient` | no | backs off |
| the connection breaks or times out **after** the message was handed over — anywhere in the send's commands, the envelope included — and before the reply | throws `MailUnavailableException`, cause `unconfirmed` | no | backs off; the next pass sends again under the same `Message-ID` |
| a TLS session weaker than configured, a refused credential, or a permanent `5xx` about the sender or the message | throws `MailUnavailableException`, cause `tls`, `credential` or `rejected` | no | backs off, logged as an error |

**Retrying the transfer is not idempotent, so the client never does it.**
§9.7 retries only an idempotent operation, and a message transfer whose
`250` was lost has already been delivered; a retry there is the duplicate
the intent stamp exists to record, made silently. So the pipeline retries a
connection, a greeting or an envelope refused before the data is sent, and
a `4xx` to the data itself, which is the relay saying it holds nothing; a
break mid-transfer, which says nothing, goes back to the row, whose next
pass resends over the intent stamp and counts it. An envelope stall is the
cost of one MailKit call carrying the envelope and the data together: it is
counted under `unconfirmed` and resent over its intent.

**The last row backs off rather than terminating**, because each is a
deployment fault — a relay misconfigured, a session somebody downgraded, a
sender the relay will not accept — and the row should send once it is
fixed. It is logged as an error and counted by its cause, as a refused
credential is in ADR-052's table, because it is somebody's decision and not
an outage.

**The breaker parks the queue of rows, not the messages.** A dead relay is
met by `MailHop`'s breaker once per sampling window, and while it is open a
pass does not claim at all: the rows stay `Pending` with their backoff, the
consumers go on writing rows, and nothing reaches `_error`. That is the
whole of "a dead relay is met once, not seven queues times every message",
and it needs no kill switch, because no consumer calls the relay.

**`MailHop` holds the hop's numbers in one class**, in `CarrierHop`'s shape:
the attempt timeout, the retries, the capped delay, the total — strictly
below `ServiceOptions.OperationTimeout` and outside §9.7's one-to-two-second
band for §9.7's own reason, that a third party behind an ACL is sized to the
wait above it — and the breaker's ratio, minimum throughput, sampling window
and break, sized to the worker's call rate and asserted able to open.
**SMTP is no `HttpClient`, so `AddStandardResilienceHandler` never reaches
it**; the pipeline is built by hand with Polly's `ResiliencePipelineBuilder`,
the same engine under the HTTP handler, and PR-2's plan says whether that
needs a pin of its own. It also names the worker's tick. The worker's lease
sits above `MailHop`'s total plus `ContactHop`'s, and a test holds that
inequality.

**Waiting has an end.** A row `Pending` past `DeliveryOptions.GiveUpAge` is
`Undeliverable: gave_up` — ADR-052's terminal outcome with a reason of its
own, because a notification a day late is worse than none. `DeliveryOptions`
is a bound section in `FulfilmentOptions`' form, earning its options type on
ADR-052's word as `Fulfilment` does in §15.4. The age covers every wait at
once — an order record that never arrives, a contact that cannot be read, a
relay that stays down — because the customer's experience of all three is
the same lateness.

## 5. The record

There is no Domain project, so the record's rules live in
`Notifications.Application`: a `Notification` type and a pure transition
function per step of section 4, with a table the Application suite drives
row by row, as Shipping's Domain suite drives its state table.

| From | On | To |
|---|---|---|
| — | an event, by its consumer | `Pending` |
| `Pending` without a customer | the order record names one | `Pending`, with `CustomerId`, set once |
| `Pending` | a decline whose order the customer cancelled | `Suppressed` |
| `Pending` | the owner answers that the customer does not exist | `Undeliverable: no_such_customer` |
| `Pending` | the relay refuses the recipient for good | `Undeliverable: recipient_refused` |
| `Pending` | the contact's mailbox is not one a message can be addressed to | `Undeliverable: not_a_mailbox` |
| `Pending` | the give-up age passes | `Undeliverable: gave_up` |
| `Pending` | the send is about to start | `Pending`, with `SendStartedAt` and the rendered version and languages; a second start keeps the first stamp |
| `Pending` with `SendStartedAt` | the relay accepts | `Sent` |
| `Pending` | the customer is erased | `Undeliverable: erased` |

**Every other arrival is a no-op that logs and returns, never a throw**,
for Shipping's reason: thrown from the worker it is a row retried for ever.
`Sent`, `Suppressed` and `Undeliverable` are terminal. A second message for
one event id never reaches the table — the inbox drops it — and the unique
key below is the second line behind that, not the first.

## 6. Persistence

Schema `notifications`, database `Notifications`, both connection keys, as
the scaffold names them. Three tables beside the scaffold's inbox and
idempotency-marker tables, and no outbox:

| Table | Key | Holds |
|---|---|---|
| `NotificationLog` | `NotificationId`; `(EventId, TemplateKey)` unique | `OrderId`, `CustomerId NULL`, `TemplateKey`, `TemplateVersion NULL`, `Languages NULL`, `Parameters`, `Status`, `Reason NULL`, `CreatedAt`, `SendStartedAt NULL`, `CompletedAt NULL`, `Attempts`, `NextAttemptAt`, `LockedUntil NULL`, `RowVersion` |
| `OrderRecords` | `OrderId` | `CustomerId`, `CancelledAt NULL`, `CancelReason NULL`, `CancelOrigin NULL`, `RecordedAt` |
| `ContactRecords` | `CustomerId` | `Email`, `Locale NULL`, `FetchedAt` |

**`NotificationLog` is ADR-053 rule 4's record and holds nothing that
names a person but the customer's id**: the event's id, the template's key,
version and languages, the outcome and the times. It holds **no mailbox and
no body**. `Parameters` is the set of values the template's placeholders
take — the order's id, an amount and currency, a tracking number, a
cancellation code, the event's instant — **stored as a versioned format**,
a JSON object carrying a `v` member that the worker refuses to read past the
version it knows, because a stored payload outlives the code that wrote it.
None of those values is personal data, and with the template's version they
reproduce what was sent, which is what rule 4 asks of the record. PR-1
stores the column as a bounded string, since nothing writes the format
until PR-4, which defines it and its refusal. The
customer's id is copied from the order record when the worker resolves it,
and is `NULL` until then.

**`ContactRecords` is ADR-052's contact row and the one place a mailbox
lands.** It holds the mailbox, the locale and the instant it was fetched,
and nothing else Keycloak offered. **`OrderRecords` holds a customer's id
against an order**, which is pseudonymous personal data, and so has a window
and an erasure path like the other two.

**Retention is a value of the deployment, refused rather than clamped**
(ADR-053). `NotificationsJurisdictionOptions` is the one options class that
record's rule 1 gives a service, and Notifications' is the first to hold all
three kinds of member that rule names: the **language set**, the **time
zone** dates are rendered in — an IANA id resolved at start, so a zone the
image does not know fails the host rather than the send — and three
windows. `LogRetention` is the statutory one, a terminal row's age at
deletion, and may be years because the row holds nothing personal once
erased; `ContactRetention`, refused at start below `ContactOptions`'
stale ceiling since a window shorter than the ceiling is a ceiling nothing
reaches, deletes a contact row not refreshed for its
window; `OrderRetention` deletes an order record that old. Each window is
`[Required]` with a stated bound, as `ShippingJurisdictionOptions`' are.
**Two windows pull opposite ways and are separate settings for it**: the
contact's is as short as the PII argument can make it, and the scaffold's
`InboxWindow` is as long as the longest wait plus an `_error` replay,
because §9.5 makes it a constraint and a replay older than it is a
duplicate notice. A start-up check refuses an `InboxWindow` shorter than
`DeliveryOptions.GiveUpAge`, and a log row purged before its inbox row is
fine while the reverse is not. **That check caps the give-up age at
`RetentionPolicy`'s `InboxWindow`**, a week, which is a registered code
value and not configuration: an operator lengthens the age past it only
with a code change, and the refusal's message names both numbers so the
cap is found at start rather than in a duplicate.

**`OrderRetention` has a floor the others do not**: an order record deleted
while a notification still waits on it turns that notification into a
give-up. So the purge deletes an order record only when no `Pending` row
names its order, and a test holds that.

**Erasure** is §11.7's consumer shape, owed with that extension like the
rest of it, and the path is named here because ADR-052 asks for it beside
each table's creation: delete the subject's `ContactRecords` and
`OrderRecords` rows; mark every `Pending` `NotificationLog` row naming the
customer `Undeliverable: erased`; replace the customer's id on every
`NotificationLog` row as §11.7 replaces an order's (ADR-053 rule 4). The
reader is erased after its owner, Keycloak, which ADR-052 already sequences.

The migrations are named for their tables and emitted by `dotnet ef
migrations add`: `AddNotificationLog` in PR-1, `AddContactRecords` in PR-3,
`AddOrderRecords` in PR-4, and `AddSendAndRetentionIndexes` in PR-5 for
the claim's and the purge's indexes, named for its content as Shipping's
`SplitShipmentAttemptsAndIndexClaims` is. A first migration rewritten in
review is followed
by `down -v` before the migrator's answer is believed.

## 7. Templates and rendering

**A template is an embedded resource in `Notifications.Application`**,
named `Templates/{key}.v{version}.{language}.txt`, whose first line is
`Subject: ` and the subject and whose remainder is the body. The seven keys
are the events' names in kebab case — `order-placed`, `order-confirmed`,
`order-cancelled`, `payment-declined`, `payment-refunded`,
`shipment-dispatched`, `shipment-delivered` — each at version 1 in three
languages: twenty-one files. **The cancellation's reason phrases are
three more**, `order-cancelled.v1.{language}.reasons.txt`, versioned with
their template: in code they would make a third language a code change,
against ADR-053 rule 2, and a row's version could not reproduce the reason
it gave, against rule 4. **The dotted names read to MSBuild as cultures**
and would compile into satellite assemblies, leaving the embedded set
empty; the resource item says it is not one, and a test counts the
resources. **A value dropped at intake renders as a dash** in every
language, so a message is never held back for a stranger's bad bytes.

**A version is retired only when no record of a send still names it** —
ADR-053 rule 4 — so a new version is a new file beside the old, and the
renderer sends the highest version present for the key; the old file stays
until `LogRetention` has passed over the last row naming it. Nothing in
this design deletes one.

**Each key declares its placeholders as a closed set**, and the host refuses
at start, naming the file:

- a template naming a placeholder its key's set does not hold;
- a language in `NotificationsJurisdictionOptions.Languages` with no file
  for some key at its current version;
- a subject line containing a placeholder at all.

**Every template is a service message about an order the customer placed**
(ADR-053 rule 4). A template that is not — a line of promotion, a
recommendation — is a new ADR before it is a pull request, and the review
of every template file checks it against that sentence.

**Values are formatted by the language, dates by the zone.** An amount is
the event's decimal and its ISO 4217 code, formatted with the language's
culture and never rounded here — `Money`'s fixed exponent is ADR-053's
counter-example and nothing new copies it. A date is the event's instant
converted to the deployment's zone. **Both depend on ICU and tzdata, which
the `-chiseled-extra` runtime image carries and the plain chiselled one
does not**; the worker's Dockerfile keeps `-extra`, and a test renders a
Kazakh date and amount and asserts they differ from the invariant culture's,
so an image that loses ICU fails a test rather than a customer.

**A customer with no usable locale is sent every language of the set in one
message**, the bodies in the set's order separated by a fixed rule, and the
subjects joined by ` / `.

## 8. What the service sends is somebody else's input

Four of a notification's values were written by another service, and the
tracking number by a carrier before that. So rendering is the platform's
first output encoding, and the rules are:

- **No interpolated value reaches a header.** Subjects take no placeholder
  (section 7), the sender is `MailOptions.From`, and the recipient is a
  `MailboxAddress` built by MimeKit's parser from the contact row. A mailbox
  that does not parse, or contains CR or LF, is `Refused(NotAMailbox)` at
  the adapter and `Undeliverable: not_a_mailbox` on the row rather than
  sent, with a test that tries it through a realm user whose email carries
  one. The contact adapter stores such a mailbox as Keycloak gave it: ADR-052
  has no outcome for a malformed answer, and the channel is the layer that
  knows what an address is. Keycloak's own user profile refuses that email,
  so the test container's profile is loosened for the fixture alone, and a
  self-check proves the staging took rather than letting the case pass
  vacuously.
- **Every value is encoded for `text/plain`**: each is bounded in length
  and holds no control character and no bidirectional override. A tracking
  number carrying one is refused at the consumer that stores it, which
  records the notification without the value rather than faulting, since a
  fault would send a stranger's bytes to `_error`.
- **No URL is rendered.** The tracking number is; a link to a carrier would
  have to be rebuilt from a host this service's configuration allow-lists,
  and Shipping already stores none. A link is a second template version
  and a configured host, owed with the first real carrier.

## 9. The anti-corruption layers

Two, and each failure either can meet is classified here — transient, dead
dependency, or an *answer* — because nothing in this service may throw into
a queue.

**The mail port** is `IMailChannel.SendAsync(message, ct)`, in the domain's
words: it takes a recipient, a subject, a body, the event's id and the
template's key from which the adapter builds the `Message-ID`, and the
languages, and returns `Accepted` or `Refused` with one of section 4's two
reasons, throwing `MailUnavailableException` with a cause for everything else
that table names. **A relay's exception may quote the mailbox**, in its message
or in the server's response text, so the adapter catches at the port and
rethrows with the event's id, the template's key — the row's unique key — and
the SMTP reply code, with no inner exception and none of the server's text. The
logs then carry an identifier — and no `Fault<T>` exists to carry one, since the
worker is not a consumer — and a test reads the exported records for a known
recipient, and the `_error` queue, and finds neither.

**`MailOptions`** binds `Mail:Host`, `Mail:Port`, `Mail:From`,
`Mail:Security` — `StartTls` or `None` — and `Mail:UserName` and
`Mail:Password`, and the hop registers in its own `AddMailChannel` that
`Program.cs` calls with the host's environment, as ADR-055 has every hop
registered. **`None` is refused there outside Development**, in the shape
of §11.5's discovered-endpoint rule, and under `StartTls` the
certificate is validated by the platform's trust store: no
`ServerCertificateValidationCallback` that returns true exists anywhere in
the service, and a test asserts the adapter refuses a self-signed relay
outside Development. The credential is required outside Development and
absent in Compose, where Mailpit accepts unauthenticated submission;
**it is a new credential**, so it takes `docs/secrets.md`'s five places in
PR-2 but the chart's, which is PR-6's values file.

**The contact port** is `IContactSource.GetAsync(customerId, ct)`, returning
`Found(email, locale)` or `NoSuchCustomer`; its adapter calls Keycloak's
admin API — `GET /admin/realms/{realm}/users/{id}` — through a typed
`HttpClient` registered by its own `AddContactSource` that `Program.cs`
calls, as ADR-055 has every hop registered, with `ClientCredentialsHandler`
— its token requested under the `roles` scope, whose mapper is what writes
`resource_access`, since the client holds no `commerce-api` — inside its
resilience pipeline
and `ContactHop`'s five numbers, inside §9.7's bands because Keycloak is
this deployment's own. ADR-052's outcomes are its contract: a `404`, a user
with `enabled: false`, or a user with no email is `NoSuchCustomer`; anything
transient throws; a `401` or `403`, or a token whose
`resource_access.realm-management.roles` is not exactly ADR-052's set,
throws `ContactSourceRefusedException`, which backs off as a transient fault
does and increments `notifications.contact.refused`. The adapter reads
`email`, `enabled` and `attributes.locale` and **binds nothing else** —
the user representation's name and other attributes never reach a type.
The locale is bounded to a BCP 47 shape and dropped, not refused, when it
is not one.

**Each outbound dependency**, classified as
[§9.8](../../backend-architecture/09-messaging.md) classifies a failure, with
ADR-053 rule 3's place of processing in the last column:

| Dependency | Unreachable | Answers no | Chose | Runs |
|---|---|---|---|---|
| Keycloak, for the contact | served from a contact row younger than the stale ceiling, otherwise the row backs off; consumers unaffected | `Undeliverable: no_such_customer`; a refused credential backs off and is counted | availability | the same deployment, by ADR-053 |
| Keycloak, for the client token | as above | a refused credential is a deployment fault, logged without the secret | availability | the same deployment |
| The relay | the breaker opens and no pass claims; rows back off | `Undeliverable: recipient_refused`; a misconfiguration backs off and is counted by cause | availability | a chart value; a processor shown a mailbox, a language and a message about an order, and the spec of a real relay names its country |

**ADR-053 rule 3's naming of the relay's country is owed with the first
real relay**, because Mailpit runs wherever Compose does and is shown no
real mailbox — stated here so that the deferral is a decision.

## 10. Messaging

**`notifications-events`** is one queue binding the seven events, declared
as §9.5 prints a receive endpoint: the inbox filter outside the in-memory
outbox, and `RetryPolicy.Standard` as Shipping's endpoint takes it — §9.8
gives the mapping exceptions' exclusion to a command endpoint alone. It
has **no delayed redelivery and no second ladder**, because no consumer can
meet a fault that is a wait: each writes rows in its own database and calls
nothing. One queue rather than seven, because the consumers share every
property a queue would separate — the same database, the same lack of
calls, the same rate — and seven queues would be seven backlog series and
seven `_error` queues for one service's one failure mode.

**Each consumer writes in one transaction**: its `NotificationLog` row, and
for the three Ordering events the order record — created by whichever of
the three arrives first, its cancellation columns set by `OrderCancelled`
and never cleared. A record written by `OrderCancelled` before `OrderPlaced`
is the tombstone the other services keep, and the late `OrderPlaced` finds
it and leaves its cancellation alone; the two commute, and a test runs both
orders.

`MessagingRegistrationTests` asserts every cell of §3.2's Consumes column
for Notifications has an `AddConsumer`, and the worker's broker-binding test
over a live broker that each is bound to `notifications-events`. The broker
account `notifications-svc` is **the narrowest in the estate**: its `write`
reaches its own endpoints and the fault exchanges and **no
`Common.Contracts` exchange at all**, because §3.2 gives it nothing to
publish — ADR-036 used this service to argue that the least valuable one was
enough, and `check_permissions.py` holds the entry to that. It lands in PR-1,
since PR-4's queue needs it and the gate holds the entry to the code either
way.

## 11. Configuration and deployment

**PR-1's keys are Shipping's, renamed.** `ConnectionStrings__Notifications`,
`ConnectionStrings__NotificationsMigrator`, `ConnectionStrings__RabbitMq`
with the `notifications-svc` account, `Identity__Authority`, which the
rendered host binds at start, and `OTEL_EXPORTER_OTLP_ENDPOINT`.
PR-2 adds the six `Mail__*` keys, of which Compose sets four — the
credential is absent there; PR-3 adds `ContactSource__BaseUrl`,
`ContactSource__Realm` and the three `Identity__Client__*` keys; PR-4 adds
the five `Jurisdiction__*` keys; PR-5 adds `Delivery__GiveUpAge`. **Every new
credential is `docs/secrets.md`'s five places**, split as §15.4's own rule
splits them — a key joins when a host's code reads it. The client secret is
minted in PR-3, which takes the realm, the rotation row, the exception row,
§15.4's inventory rows and the Compose and fixture places, since the adapter
first reads the keys there; the relay's password is PR-2's; and the chart's
place for both is the chart's values file, which cannot exist before PR-6
and needs no edit to `docs/secrets.md` when it does. **The give-up age
defaults to a day**, in Compose and in the chart: the "a day late is worse
than none" of section 4, as a value an operator lengthens during an
outage.

**No port is published by the worker.** Nothing dials it, so the health
endpoint is bound inside the container and never mapped, and the Compose
unit declares no `healthcheck`, as Shipping's does not: the runtime image
is chiselled and carries no probe binary.

**PR-6's chart is Shipping's, renamed**: `service.enabled: false`,
`ingress.enabled: false` and `redis.enabled: false`, each written down as
§15.3 asks. Every key the host binds at start arrives as a capability the
render refuses to leave empty: `mail` for the relay's six keys,
`contactSource` for Keycloak's address and realm, `jurisdiction` for the five
values, `delivery` for the give-up age, and the client credentials through
the capability the BFF's and Shipping's charts already use. Autoscaling is
off and the replica count is three, by Shipping's argument: CPU is the wrong
signal for a workload that waits on a relay. The canary row declares
`consume` and carries an `httpExemption`, as Inventory's and Shipping's do,
and the analysis needs no change (ADR-047). **The readiness set is SQL and
the bus, and a test asserts the relay and Keycloak are not in it**:
readiness here gates a rollout and nothing else, since no Service routes to
the pod and a consumer consumes whether it is ready or not, so a third
party in it could only ever block the deploy that fixes it.

## 12. Observability

**No outbox gauges**, by section 1.

**Five instruments on a `Notifications.Outbound` meter**:
`notifications.mail.unavailable`, per attempt that does not end in an answer,
with a `cause` attribute from section 4's table — so a TLS refusal is counted
apart from an outage on one instrument; `notifications.mail.resent`, per send
started over an existing intent stamp; `notifications.contact.refused`, which
section 9 argues; and `notifications.waiting`, an observable gauge of `Pending`
rows past their first backoff, by the step they wait on — `order_record`,
`contact`, `relay`; and `notifications.overdue`, an observable gauge in
`shipping.shipments.overdue`'s form — a duration in seconds, how long the oldest
`Pending` row due for a pass has waited unclaimed past two ticks, exported as
`notifications_overdue_seconds`. The second is the one that says three replicas
are too few: a row no pass has reached is invisible to the first, which counts
only rows a pass has already backed off. §13.2's export names meters one by one,
so PR-2 adds the `AddMeter` line.

**Notifications' latency number is delivery lag, and the rules that read it
already exist**: Shipping's PR-7 added `DeliveryLagHigh` and
`QueueBacklogGrowing`, and both select every working queue, so
`notifications-events` is covered the day it is declared. What PR-6 owes is the
shared runbook's half for this service — that delivery lag stops when a consumer
starts and never sees the worker's wait on a relay, and that the waiting gauge,
by step, is the signal for that, and the overdue gauge for too few replicas —
and the dashboard's panels for both. **The outbox dashboard's service variable
reads services that publish outbox gauges**, so a pure consumer never appears on
it, delivery-lag panel included; PR-6 widens the variable to services publishing
either series. A rule over the gauge is not added: a growing waiting set during
a relay outage is the breaker working, and the lag of a give-up is already a row
with a reason an operator can select.

**No log line holds a mailbox or a body**, as an attribute or inside an
exception's text: the log takes the notification's id, the order's id and
the customer's id, §13.4's own example. `SensitiveKeys` is not widened. A
test exports the logs of a run over a known mailbox, and reads the
`_error` queue, and finds none of it.

## 13. Testing

By [§12](../../backend-architecture/12-test-strategy.md)'s layers; the
container tests are `Category=Integration` and never skipped. The suites are
`Notifications.Application.Tests` and `Notifications.Worker.Tests`, with
`Notifications.TestSupport` beside them; there is no Domain suite because
there is no Domain project.

- **Application**: every row of section 5's table and every refused arrival
  as a no-op; each consumer against a fake store, the order record's three
  writers in every order; ADR-049's suppression across origin present,
  absent with each reason, and the decline arriving before and after the
  cancellation; the renderer over every key × language, the placeholder and
  missing-file refusals, the subject rule, the multi-language message; the
  parameters' format refusing an unknown `v`; the architecture tests.
- **Worker**, over SQL Server, RabbitMQ and Mailpit containers, a real
  Keycloak for PR-3's adapter, and WireMock.Net where a fault must be
  staged:
  - the mail adapter against every row of section 4's table, the TLS
    refusal outside Development, the exception that carries no mailbox, and
    **the Kazakh-script subject and body** — `ә ғ қ ң ө ұ ү һ і` — read back
    intact through Mailpit's API;
  - the contact adapter against ADR-052's five outcomes, a disabled user, a
    user with no email, an email carrying CR or LF, a locale that is not
    one, and **a token Keycloak issued to `notifications-worker` accepted
    and one issued to a client without the grant refused** — against a real
    Keycloak in its own collection; the send worker meets the CR or LF
    mailbox through a stub of the same answer, since a test class joins one
    collection and the two halves meet at `ContactLookup.Found`;
  - **the inequality** over `MailHop`'s and `ContactHop`'s constants in
    `Every_attempt_and_every_bounded_delay_fit_inside_the_total`'s shape,
    the lease above their sum, and **an opened circuit makes no call**;
  - **two workers overlapping claim one row once**, staged; **a pass that
    throws leaves the host running**; **a lapsed lease is taken by another
    pass**;
  - **a dependency dies**: with the relay stopped, rows stay `Pending`,
    every `_error` queue is empty, and when it returns they send; with
    Keycloak refusing, the same; a give-up past its age is terminal and not
    retried;
  - **a crash between the relay's accept and the commit**, staged: two
    deliveries in Mailpit with one `Message-ID`, one `Sent` row and the
    resent counter at one;
  - the log and fault export of section 12;
  - **the fixture is ADR-053's made-up deployment**: it binds
    `NotificationsJurisdictionOptions` from a language set, zone and windows
    that are neither Compose's nor production's, all seven notifications
    render under it with no code changed in PR-4, and send under it in
    PR-5, a date at the edge of a
    day lands on the zone's side of it, and a missing language file or zero
    window fails the host at start;
  - `MessagingRegistrationTests`, the readiness-set assertion, and the
    `InboxWindow` refusal.
- **PR-3's realm half**, in the suites ADR-052's table names:
  `RealmClientTests` and `RealmImportTests`' secret test turned to the set of
  three credentialed clients.
- **The order journey**, in PR-5's worker suite: an order's placed,
  confirmed, despatched and delivered events through the real
  `notifications-events` queue produce four `Sent` rows and four messages in
  Mailpit, and a cancelled order its cancellation with its reason phrase.
  **Not in `Platform.IntegrationTests`**: §12.1 and §12.6 give that project
  contract shape by reflection and argue against a container set there, and
  the same request for Shipping was declined on those grounds; admitting the
  leg is a rule moved, an ADR before it is a test. The full journey across
  the real publishers is phase 4's, and that ADR with it.
- **The scaffold's suite**, in PR-1: the pure-consumer render's tree, and
  that every gate in section 2's list covers the Notifications projects by
  selector — a test whose subject is what each gate is looking at.

## 14. The chapters that move, and the ones that do not

**ADR-052's closing table is the owner of "the places that say the BFF is
alone"**, and Shipping's spec assigned each row to a Shipping PR. Those PRs
made each place say two; this service makes it three, and every row is
re-read rather than re-assigned, because a sentence rewritten for Shipping
may now count correctly by naming a set rather than a number. **The rule for
each is to name the set or cite ADR-052, never to write "three"**, so the
fourth credentialed host moves no prose. The asserted rows are what turn
red, and they go to the PR that turns them:

| ADR-052's row | Taken by |
|---|---|
| §2.2's edges from Notifications to Keycloak, the read and the client credentials, drawn as Shipping's pair is | 3 |
| §3.1's paragraph naming ADR-052's reads, where Shipping's sentence lives; §3.2's table has no column for a read | 3 |
| §4.1's Ordering sample, "Outbound identity belongs to the hosts that call a peer" | 3 |
| §9.7's "a host that holds client credentials is a host that calls a peer" | 3 |
| §11.5's table of realm objects, its counting sentences, "the one suite that runs a real Keycloak" and its last paragraph | 3 |
| §12.1's outbound-hop row and §12.4's one Keycloak suite | 3 |
| §14.1, the client secret among the Compose defaults and Ordering's comment; §14.2's "only a host that calls another service presents them" | 3 |
| Appendix B's Testcontainers and JWT rows, which name one place an identity provider runs | 3 |
| `ServiceIdentityOptions`' summary and `realm_check.py`'s worker-client comment | 3 |
| §15.1 and §15.4, the client's inventory rows | 3 for §15.4; 6 for §15.1 |
| `_helpers.tpl` — asserted | 6 |
| `smoke.sh` — no longer red: it reads the credentialed set from the descriptors; its two source checks naming hosts become one loop over every chart | 6 |
| `RealmClientTests` — asserted | 3 |
| `RealmImportTests` — asserted; the secret test turns red, the vocabulary test is re-read and stays green | 3 |
| `realm-export.json`, the client, and the `web-bff` description that counts the others | 3 |
| `docs/secrets.md` | 3, and 2 for the relay's password |
| `docs/repo-map.md` and `CLAUDE.md` | not moved: neither counts credentialed hosts today |
| §11.7 | not moved; section 2 says why |

**The places outside that table.**

- **§4.5** loses its sentence that the second mode is owed, and **§2**
  names Notifications among the services reaching no Redis, **§7.5** gains
  the empty dispatcher's paragraph and **§9.5** the optional outbox's
  sentence, in PR-1.
- **§14.1** gains Mailpit, **§12.7** the relay's row, **§9.7** `MailHop`
  in its list of third-party hops, and **Appendix B** MailKit, MimeKit and
  Polly.Core, in PR-2.
- **§15.4's inventory** gains the relay's keys in PR-2, the jurisdiction's
  in PR-4 and the give-up age in PR-5, and its sentence naming the options
  types that earned one gains `Mail`, Notifications' `Jurisdiction` and
  `Delivery` in the same PRs.
- **§15.3**'s replica-count paragraph and its credentials paragraph and
  callout move in PR-6 — the latter to "a host that calls out under a grant
  of its own" rather than "a host that calls a peer", which Keycloak is
  not. Its sentence giving Notifications a chart with no Service is already
  true, and its Redis paragraph lists no charts.
- **No contract moves**, **§3.2's row is already complete**, and **§10
  gains no route**.
- **Appendix C gains no row.** §4.1's tree already names Notifications.

`/validate-blueprint` runs on every PR above that edits a chapter.

## 15. What this design deliberately does not do

- **No second channel.** Section 1.
- **No HTML body and no link.** Sections 1 and 8.
- **No template engine.** Section 1.
- **No contract change for `CustomerId`.** Section 1.
- **No realm internationalisation.** Section 1.
- **No Redis and no outbox.** Section 1.
- **No consent, no preference centre, no unsubscribe.** Every message is a
  service message (ADR-053 rule 4); the day one is not, an ADR comes first.
- **No erasure consumer.** Section 6 names the path; §11.7's extension
  brings it.
- **No named relay and no processor's country.** Section 9.
- **No journey test.** Phase 4's.
