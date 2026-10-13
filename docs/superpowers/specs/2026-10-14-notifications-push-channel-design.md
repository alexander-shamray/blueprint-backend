# Notifications — the push channel

Design spec, frozen at write time, dated and named for its subject as the
Notifications spec beside it is. Where this document and the blueprint
disagree, the blueprint wins. It is the successor that #474's decision
comment asked for: the 2026-10-02 spec's section 1 refused a second channel
because no record gave a device token an owner, and that refusal stays as
written, as the record of why push waited.

**What is already decided, and where.** The owner's decisions on #474 of
2026-10-12 choose both providers — FCM for Android and the web, APNs for iOS
over token-based auth — behind one push port that is not a generalisation of
`IMailChannel`, each provider a third party behind its own ACL, hop budget
and breaker, and any of the seven events deliverable by push.
[ADR-117](../../backend-architecture/adr/ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md)
decides the device token: one row per installation, registered through the
BFF with a per-installation sequence, pruned after
`DeviceTokenOptions.StaleAfter`, deleted on a provider's unregistered answer
only while it still holds the token the send used, and cleared by an
erasure.
[ADR-118](../../backend-architecture/adr/ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md)
puts registration and preferences in `Notifications.Api` and the push
adapters in the worker.
[ADR-120](../../backend-architecture/adr/ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md)
decides the consent journal, the required pairs, that a send is decided when
it is made, and that each push delivery row names the entry it was decided
under. The 2026-10-02 spec owns everything about the mail path this document
does not change.

This document does not restate any of that. It records what those records
leave open, the decisions taken on each, and the pull requests that build
them, which are #474's items 3 and 5.

## 1. What the records leave open, and the answers

**The port.** **`IPushChannel.SendAsync(OutboundPush push, ct)`**, returning
`PushResult.Accepted` or `PushResult.Refused(PushRefusal reason)` and throwing
`PushUnavailableException` with a `PushFault` cause for everything else — the
mail port's shape, with a vocabulary of its own, because a provider's answers
are not a relay's:

| | Cases | Meaning |
|---|---|---|
| `PushRefusal` | `Unregistered` | The provider says the token is no longer valid for any app: FCM's `UNREGISTERED`, APNs' `410` |
| | `InvalidToken` | The provider says the token is not one it will deliver to for this sender: FCM's `SENDER_ID_MISMATCH`, APNs' `BadDeviceToken` and `DeviceTokenNotForTopic` |
| `PushFault` | `Transient` | Unreachable, `429`, `5xx`, or the attempt timed out before a byte was sent |
| | `Unconfirmed` | The request was sent and no answer came: it may have been delivered |
| | `Tls` | The handshake failed |
| | `Credential` | The provider refused this deployment's credential: FCM's `401` and `THIRD_PARTY_AUTH_ERROR`, APNs' `403` |
| | `Rejected` | The provider refused the request itself: FCM's `INVALID_ARGUMENT`, APNs' other `400` reasons |

**Both refusals end the delivery and delete the device row**, under ADR-117's
guard: in the transaction that records the outcome, and only while the row
still holds the token the send used. The owner's decision names "unregistered
or invalid token" together, and ADR-117 names the unregistered half; a token a
provider will never deliver to for this sender is as dead as an unregistered
one, and the app's next start registers a fresh one. **FCM's
`INVALID_ARGUMENT` is a fault and not a refusal**, because FCM answers it for a
malformed message as well as a malformed token: a defect in the platform's own
payload would otherwise delete every device it was sent to.

**One port, two providers.** The worker resolves `IPushChannel` to one
internal router that picks the provider from the device row's platform, so the
send path names no provider, as decision 1 has it. Each provider is an
internal `IPushProvider` of its own — `FcmPushProvider` and
`ApnsPushProvider` — with its own `HttpClient`, its own hop and its own
breaker, and its own registration method beside `AddMailChannel`.

**No vendor SDK.** Both adapters are written over `HttpClient` and
`System.Security.Cryptography`, and no package joins Appendix B. The Firebase
Admin SDK would bring the Google API client libraries, make its own HTTP calls
outside the hop and throw its own exceptions, so the ACL could not classify an
answer from the response it was given; the APNs libraries for .NET are small
and unmaintained. What the adapters write themselves is two signed tokens: an
RS256 assertion exchanged for an FCM access token under OAuth 2.0's JWT bearer
grant, and an ES256 provider token for APNs. Each is cached and renewed before
it expires — the FCM access token at the lifetime the exchange returns, less a
margin, and the APNs token every forty minutes, inside Apple's window of no
more often than every twenty and no less often than every sixty.

**One delivery row per device, beside one notice row per channel.** Mail is
one event to one mailbox, and push is one event to as many devices as the
subject has registered, so a terminal outcome — a refusal, a give-up — must
land on one device's row, or the event's row ends up holding several that
contradict one another. So `NotificationLog` gains a `Channel` and holds one
notice per event and channel; a push notice's pass **fans out** into one
`PushDeliveries` row per device and ends; and each delivery row is sent,
retried and ended alone.

**Two workers, not one.** The existing `SendWorker` claims notices of both
channels: it sends a mail notice as it does today and fans out a push notice.
A new **`PushSendWorker`** claims delivery rows and calls the port. They are
separate loops because their waits are separate: a mail notice waits on the
relay and Keycloak, a delivery on a provider, and the mail breaker's park,
which today stops every claim, must not stop pushes, nor a provider's stop
mail. `SendWorker`'s park therefore narrows to mail notices, and
`PushSendWorker` parks a platform's rows while that platform's breaker is
open, claiming the other's.

**Which events push.** All seven, each a service topic under ADR-120, on by
default, with no requirement in the reference deployment's values; whether a
push is sent is decided by the subject's journal, and whether it is shown by
the operating system's permission.

**A duplicate collapses rather than stacks.** A send resent under its intent
after a crash, or answered `Unconfirmed` and retried, may reach the device
twice. Every push carries the event's id as its collapse key — APNs'
`apns-collapse-id`, FCM's `android.collapse_key` and the web push `Topic`
header — so a second copy replaces the first on the device rather than showing
beside it. APNs' `apns-id` is the delivery's id, so a provider's log line and
the platform's row name one another.

**What a push carries.** A title, a body and two data fields, `order` and
`topic` — the order's id and the template key — from which the app builds its
deep link. No amount, no address and no name: a push is shown on a lock screen
to whoever holds the device, and both providers are processors shown its
whole content.

## 2. The anti-corruption layers

**`OutboundPush`** holds the platform, the app id, the token, the title, the
body, the order's id, the template key, the collapse key, the delivery's id
and the correlation id. The token is in memory only for the length of the
call: it is never a log attribute, a span attribute, a metric label or part of
an exception's message (ADR-117), and the adapters rethrow at the port with
the delivery's id and the provider's status code, no inner exception and none
of the response's text, as the mail adapter does with the relay's.

**FCM.** `POST {BaseUrl}/v1/projects/{ProjectId}/messages:send` with the
access token as a bearer, and the message's `token`, `notification`, `data`,
`android.collapse_key` and `webpush.headers.Topic`. The answer's
`error.details[].errorCode` is what is read, and the HTTP status only where
none is given:

| Answer | Outcome |
|---|---|
| `200` | `Accepted` |
| `UNREGISTERED` | `Refused(Unregistered)` |
| `SENDER_ID_MISMATCH` | `Refused(InvalidToken)` |
| `INVALID_ARGUMENT` | fault `Rejected` |
| `THIRD_PARTY_AUTH_ERROR`, `401`, `403` | fault `Credential` |
| `QUOTA_EXCEEDED`, `UNAVAILABLE`, `INTERNAL`, `429`, `5xx` | fault `Transient` |

**APNs.** `POST {BaseUrl}/3/device/{token}` over HTTP/2, with `authorization:
bearer` and the provider token, `apns-topic` set to the app id,
`apns-push-type: alert`, `apns-priority: 10`, `apns-collapse-id`, `apns-id`,
and `apns-expiration` at the notice's give-up instant, so Apple drops a push
the platform would itself have given up on:

| Answer | Outcome |
|---|---|
| `200` | `Accepted` |
| `410` | `Refused(Unregistered)` |
| `400 BadDeviceToken`, `400 DeviceTokenNotForTopic` | `Refused(InvalidToken)` |
| any other `400` | fault `Rejected` |
| `403` | fault `Credential` |
| `429`, `5xx` | fault `Transient` |

**HTTP/2 is required outside Development.** APNs refuses HTTP/1.1, so the
APNs client asks for exactly HTTP/2 over TLS. An `http` base URL is refused at
start outside Development, the shape of the mail adapter's refusal of
`Security=None`; in Development the client accepts HTTP/1.1 from an `http`
base URL, which is what the simulator in section 7 serves.

**Hops and breakers.** `FcmHop` and `ApnsHop` each own an attempt timeout, a
retry and a total timeout, and a breaker, in `MailHop`'s shape and outside
[§9.7](../../backend-architecture/09-messaging.md)'s bands by that section's
rule 1, sized to the leased row. Each pipeline is hand-built as
`MailPipeline` is — total timeout, retry, breaker, attempt timeout — and
retries `Transient` and an attempt timeout only, never `Unconfirmed`, which
the row's backoff retries instead so a duplicate is at most one per backoff.
The numbers are the code's; the design's starting values are `MailHop`'s, and
rule 2's sizing of each breaker to its own call rate is the plan's to argue,
since one event is one mail and up to ten pushes.

**Options**, each bound with `ValidateOnStart` in its own registration
method that `Program.cs` calls with the host's environment:

| Class | Members | Refused at start |
|---|---|---|
| `PushOptions` | `Apps`, each an app id and its platform | an app id named twice, or a platform outside the pair |
| `FcmOptions` | `BaseUrl`, `TokenUri`, `ProjectId`, `ClientEmail`, `PrivateKey` | a missing member while `Apps` names an `fcm` app; an `http` URL or a missing key outside Development |
| `ApnsOptions` | `BaseUrl`, `TeamId`, `KeyId`, `PrivateKey` | a missing member while `Apps` names an `apns` app; an `http` URL or a missing key outside Development |

A deployment may run one provider: with no app of a platform, that provider's
options are not required and its adapter is not registered, and a device row
of that platform cannot exist because registration refuses an app id the
deployment does not name. **In Development a missing key is generated at
start**, an ephemeral RSA or EC key that the simulator never verifies, so no
key is committed for Compose and none is a sample. `PushOptions.Apps` is the
list ADR-117's BFF check reads, and the BFF and both Notifications hosts are
each given it from one chart value.

**Each outbound dependency**, in the 2026-10-02 spec's section 9 table's
form:

| Dependency | Unreachable | Answers no | Chose | Runs |
|---|---|---|---|---|
| FCM | its breaker parks its platform's rows; rows back off; mail and APNs unaffected | the delivery ends and the device row is deleted under ADR-117's guard; a misconfiguration backs off and is counted by cause | availability | Google, a processor shown a token, a title, a body and an order's id |
| APNs | as FCM, for its own platform | as FCM | availability | Apple, a processor shown the same |
| Google's token endpoint | the FCM call faults `Transient` and backs off | fault `Credential` | availability | Google |

**ADR-053 rule 3's naming of each provider's country is owed with the first
real deployment**, as the relay's is: the simulator runs wherever Compose does
and is shown no real token.

## 3. The record

**`NotificationLog` gains two columns.** `Channel`, `email` or `push`, with
the unique key becoming `(EventId, TemplateKey, Channel)` and every existing
row backfilled `email`; and `ConsentEntryId NULL`, the ADR-120 entry a mail
notice was decided under. `NotificationStatus` gains **`Dispatched`**, the
terminal state of a push notice that has fanned out: its outcome is its
deliveries'. `Suppressed` gains the reason **`opted_out`**, and `Undeliverable`
gains **`no_device`**.

**`PushDeliveries`**, one row per push notice and installation:

| Key | Columns |
|---|---|
| `DeliveryId`; `(NotificationId, InstallationId)` unique | `NotificationId`, `InstallationId`, `Platform`, `CustomerId NULL`, `ConsentEntryId NULL`, `TemplateVersion NULL`, `Languages NULL`, `Status`, `Reason NULL`, `CreatedAt`, `SendStartedAt NULL`, `CompletedAt NULL`, `Attempts`, `NextAttemptAt`, `LockedUntil NULL`, `CorrelationId`, `TraceParent NULL`, `TraceState NULL`, `RowVersion` |

It holds **no token**: the send reads the device row's token when it sends, so
a rotation is honoured and ADR-117's guard has the token it needs in memory.
It holds no parameters either: the render reads its notice's.

**The delivery's state rules** are the notice's, in `Notifications.Application`
beside them: `Pending` to one of `Sent`, `Suppressed: opted_out`,
`Undeliverable` with `unregistered`, `invalid_token`, `no_device` or
`gave_up`, each a move that returns false when the row is already terminal,
and `StartSend` stamping the template version, the languages and the entry
once.

## 4. The send path

**A mail notice** is sent as the 2026-10-02 spec's section 4 has it, with one
step added before the contact read: the pair `(topic, email)` is checked
against `NotificationsJurisdictionOptions.Required` and, when not required,
against the subject's newest journal entry or the service default. Opted out
ends it `Suppressed: opted_out`; otherwise `StartSend` stamps the entry with
the template version, so the record of the send names it. In the reference
deployment every email is required, so no mail notice reaches the journal.

**A push notice** shares the mail notice's first five steps — the give-up
age, the order-record wait, ADR-049's suppression, the erasure check, the
customer committed once — and then:

1. **The pair is checked** as above, for `push`; opted out ends it
   `Suppressed: opted_out`.
2. **The devices are read**: the subject's rows that hold a token. None ends
   it `Undeliverable: no_device`.
3. **The contact is read** through the shared `ContactReads`, for the
   language alone, under ADR-052's outcomes; the mailbox is not used.
   `NoSuchCustomer` ends it `Undeliverable: no_such_customer`.
4. **One transaction** inserts a `Pending` delivery per device, carrying the
   languages and the entry, and moves the notice to `Dispatched`. The unique
   key makes a pass repeated after a crash insert nothing twice.

**A delivery** is claimed by `PushSendWorker` with the notice claim's shape —
`UPDATE TOP (n) … WITH (UPDLOCK, READPAST, ROWLOCK)` under a lease above the
larger of the two hops' total timeouts — leaving out the rows of a platform
whose breaker is open, and is sent:

1. **The give-up age**, measured from its notice's creation, so a push is
   never later than its mail would have been allowed to be: past it,
   `Undeliverable: gave_up`.
2. **The pair is checked again**, because ADR-120 decides a send when it is
   made and a delivery may wait through backoff for hours: opted out ends it
   `Suppressed: opted_out`, and a newer entry that still opts in is the one
   stamped.
3. **The device row is read by installation.** Gone, cleared, holding another
   subject, or holding another platform, it ends `Undeliverable: no_device`;
   ADR-117's moved device must never show the subject before.
4. **The render** uses the push template at the stamped version, or the
   current one on a first send, and **`StartSend`** commits the intent before
   the call; a delivery that already holds an intent is a resend, counted.
5. **The port is called.** `Accepted` commits `MarkSent` with
   `CancellationToken.None`, as the mail completion does. Either refusal
   commits `MarkUndeliverable` with its reason and, in the same transaction,
   deletes the device row where it still holds the installation and the
   token the send used. A fault is logged by cause and the row backs off by
   the notice's formula.

**The waits stay visible.** `notifications.waiting`'s `step` tag gains
`devices` for a push notice and `fcm` and `apns` for deliveries, and
`notifications.overdue` reads the oldest pending row of either table.

## 5. Templates

**Seven push keys beside the seven mail keys**, versioned and checked at
start as section 7 of the 2026-10-02 spec has mail's: a closed placeholder set
per key, a file for every language of the set at the key's current version,
and the highest version present sent. A push file is a title line and a body.
**A push key's placeholders are a subset of its mail key's and carry nothing
personal beyond the order's reference**, checked in review against this
sentence as mail's promotion rule is. A customer with no usable locale is sent
every language of the set, joined by ` / ` in the title and by the mail
template's rule in the body. The rendered body is bounded so the payload fits
under both providers' 4 KB ceiling, and a test renders the longest key in
every language of the test jurisdiction and measures it.

## 6. Erasure and retention

**`PersonalDataDeleteRequested`** deletes the subject's pending deliveries
and anonymises its ended ones — `CustomerId` and `Languages` cleared — beside
the notices, in the handler's one transaction; ADR-117's device rows are
cleared and ADR-120's journal anonymised there too, and the handler's count
includes the rows of the three tables this design adds to its reach.
**Retention** ages an ended delivery with its notice, by `LogRetention` from
the notice's completion, and never ages a notice while a delivery of it is
pending. ADR-117's stale pass
and ADR-120's journal pass run in the worker's existing retention service.

## 7. The simulator

**`deploy/compose/push-simulator/`**, WireMock.Net mappings in the carrier
simulator's shape, one engine and one set of mappings for the Compose
container and the adapters' tests. It serves FCM's send path, Google's token
endpoint and APNs' device path behind one port, and is scripted by the
token's prefix, so a test drives each outcome rather than waiting for it:

| Token | FCM | APNs |
|---|---|---|
| any other | `200` | `200` |
| `unregistered-` | `404 UNREGISTERED` | `410 Unregistered` |
| `invalid-` | `403 SENDER_ID_MISMATCH` | `400 BadDeviceToken` |
| `rejected-` | `400 INVALID_ARGUMENT` | `400 BadTopic` |
| `unavailable-` | `503 UNAVAILABLE` | `503` |
| `slow-` | `200`, after the attempt timeout | `200`, after the attempt timeout |

`slow-` is what a test uses for a token that rotates under a send: it registers
a new token for the installation while the call is held, and asserts the
`unregistered-` answer that follows deletes nothing. The simulator serves
HTTP/1.1 and verifies no signature, which is why Development accepts both;
the signatures are proved in unit tests against keys the tests generate.

## 8. Configuration and deployment

**The worker's chart** gains `push`, `fcm` and `apns` blocks, the two private
keys as secrets in §15.4's shape, and the base URLs as values, ADR-053 rule
3's chart values. **`docs/secrets.md`** gains the FCM service account's key
and the APNs signing key, each with its five places and a rotation row: the
FCM key is rotated in Google's console and the APNs key in Apple's, and both
are deployment-held, never the BFF's. **§15.4's inventory** gains the push
keys in the pull request that binds each, and Compose gives the worker the
simulator's base URL for both providers and no key.

## 9. Observability

The `Notifications.Outbound` meter gains `notifications.push.unavailable`,
tagged `provider` and `cause`; `notifications.push.refused`, tagged `provider`
and `reason`; `notifications.push.resent`; and `notifications.devices.removed`,
tagged `reason` — `stale`, `unregistered`, `invalid_token` or `erased`, the
worker's four removals — so a provider's mass refusal, such as a sandbox
token sent to the production endpoint, is a series and not a silence. Every
instrument is resolved at start by the `MetricsInitialiser`. **No alert is
added**, as none watches the relay, and the shared runbook's Notifications
half gains the push steps of the waiting gauge.

## 10. Testing

- Each adapter against the simulator's mappings on loopback, every row of its
  answer table, the token never in a log record, a span or an exception.
- The two signatures, verified against the public halves of keys the test
  generated, and each token's renewal before its expiry.
- The HTTP/2 refusal of an `http` base URL outside Development, and its
  acceptance in Development.
- The fan-out against the SQL Server container: one delivery per device, none
  twice after a repeated pass, `no_device` with none, `opted_out` with a
  withdrawn entry.
- A delivery whose device moved to another subject between the fan-out and
  the send ends `no_device` and calls nothing.
- The rotation under a `slow-` send, and the guarded delete.
- The mail breaker open while pushes send, and one provider's breaker open
  while the other's platform sends.
- Erasure across the four tables, and retention never ageing a notice with a
  pending delivery.
- The longest push in every language under the payload ceiling, through the
  simulator, in Kazakh script.

## 11. The PR sequence

Five, and they are #474's items 3 and 5: the simulator rides with the
adapters, as Mailpit rode with the mail channel and the carrier simulator with
the carrier ACL, because mappings with no adapter calling them prove nothing.
Each row names its [change class](../../change-locality.md); the touch set is
the PR body's.

| PR | Subject | Class |
|---|---|---|
| 1 | `feat(notifications): Notifications.Api, a second host serving health alone` — the project and its `Program.cs`, the HTTP/2-only gRPC port, the Dockerfile and the CI image matrix, the Compose unit, `deploy/helm/notifications-api` with no migration Job and an autoscaler, its canary descriptor, `smoke.sh`'s class for a chart with a database and no Job, the rollout order, ADR-065's rows, and ADR-118's sites — §4.1, §15.2, §15.3, E.2.1 | A+D+E |
| 2 | `feat(notifications): device registration through the BFF` — the device table and its migration, `DeviceTokenOptions`, `PushOptions.Apps`, the gRPC method under the caller's token, `PUT` and `DELETE /bff/v1/devices/{installationId}` with the BFF's checks, the stale pass, the erasure's clearing, `docs/personal-data.md`'s row, and ADR-117's sites — §2.2, §3, §9.7, §10.1, §10.4, §10's wire contract, §11.5, §15.4, E.5.4, E.6.1, E.6.9, `docs/runbooks/latency.md`, `deploy/helm/catalog/Chart.yaml`, `docs/repo-map.md` | A+D+E |
| 3 | `feat(notifications): the consent journal and the preferences routes` — the journal table and its migration, `Required` and `ConsentRetention`, the gRPC methods, `GET` and `PUT /bff/v1/notification-preferences` with §10.5's precondition filter and its 412, the mail notice's journal step and `ConsentEntryId`, `opted_out`, the erasure's anonymising, the journal's retention, and ADR-120's sites | A+D+E |
| 4 | `feat(notifications): the push channel and the Compose simulator` — `IPushChannel` and its vocabulary, the router, both providers with their hops, breakers and signed tokens, `FcmOptions` and `ApnsOptions` with their refusals, the push counters, `docs/secrets.md`'s rows, the simulator's mappings and Compose unit, §14.1's, §12.7's and §9.7's amendments, E.5.4's hop rows. Nothing calls it yet | A+D+E |
| 5 | `feat(notifications): push deliveries` — `Channel` and its backfill, the push notice rows the seven consumers write, the fan-out, `PushDeliveries` and its migration, `PushSendWorker`, the seven push keys' templates, the erasure and retention of deliveries, the guarded deletes, the waiting gauge's steps, the chart's values, the runbook's push half | A+D+E |

**Order.** 1 first, since 2 and 3 serve from it; 2 then 3, since the journal's
API shares the host and its gRPC service; 4 needs nothing but the worker and
may be built beside 1 to 3 by a second session; 5 needs all four.

## 12. What this design deliberately does not do

- **No topic or broadcast messaging.** FCM topics and APNs' broadcast push
  send one message to many devices the platform does not enumerate, which
  cannot land a terminal outcome on one device's row, and every message here
  is to one subject.
- **No silent or background push.** Every push is an alert about an order;
  a content-available push wakes the app for work no record here asks for.
- **No web push outside FCM.** Browsers reach the platform through FCM's web
  client, so no VAPID keys and no third provider.
- **No per-event retry policy.** A delivery backs off as a notice does, and
  the give-up age is the notice's.
- **No promotional push.** ADR-053 rule 4 still asks for a new ADR before a
  template that is not about the customer's order, and ADR-120's journal
  records its consent when one exists.
