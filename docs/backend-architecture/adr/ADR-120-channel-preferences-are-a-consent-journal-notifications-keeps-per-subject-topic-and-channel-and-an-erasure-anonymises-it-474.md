# ADR-120 — Channel preferences are a consent journal Notifications keeps per subject, topic and channel, and an erasure anonymises it (#474)

**Decision.** Notifications keeps, for each subject, which messages it may
send them on which channel, and **that store is the consent record**
[ADR-053](ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)'s
rule 4 says the platform keeps nowhere. It is a table of Notifications' own,
written by `Notifications.Api` and read by the worker at send time, as
[ADR-118](ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md)
divides the two hosts' work.

**A preference is keyed by topic and channel.** A channel is `email` or
`push`. A topic is one of the seven order events Notifications sends a
message for, each of which is a service message, or a message class that is
not a service message, taken as a whole. The vocabulary of both is closed and
held in code, and today it holds no class but the service one: a template that
promotes
anything is still the new ADR rule 4 asks for, and that ADR names its class's
topic. **The class decides the default** for a pair that has no entry: a
service topic is on, on every channel, and any other class is off, which no
configuration changes. A promotional message is therefore sent only to a
subject whose newest entry for its topic and channel opts in.

**A pair the deployment requires is not a preference.**
`NotificationsJurisdictionOptions.Required` names the topic and channel pairs
a jurisdiction obliges the service to send, such as a confirmation it must
give on a durable medium. A required pair is sent whatever the journal holds,
takes no entry, and a write that names it is refused with
[§10.5](../10-api-gateway.md)'s 422. Start-up refuses a required pair that
names an unknown topic or channel, or a topic outside the service class, so no
deployment can require a promotion. The reference deployment's values require
the email of all seven topics, so the mail the service sends today is
unchanged by this record, and the journal governs push until a deployment
releases a pair. That adds a member to the list of what a jurisdiction varies
in ADR-053's rule 1.

**The journal is append-only.** Every act of a subject's appends an entry,
which holds the entry's id, the subject, the topic, the channel, whether it
opts in, when it was written, the client it was written through — the `azp`
claim of the caller's access token — and the entry it supersedes. The current
value of a pair is its newest entry. No entry is updated except by an
erasure, below. **An entry is superseded at most once**, which a unique key
over the entries that supersede one holds, and **a subject's pair has at most
one first entry**, which a unique key over the entries that supersede none and
still hold a subject holds. So neither two writes that name one entry nor two
first writes for one pair can both append, and the database chooses which one
lands. An append locks the entry it names and inserts only
while that entry still holds the caller's subject, so an erasure that
anonymises it lands wholly before the append, which then fails its
precondition, or wholly after it, and anonymises the new entry with the rest;
the anonymising statement holds the range of the subject's entries, as the
contact store's upsert holds its key's, so the two serialise and no entry is
inserted into a range the erasure has already passed.

**The client reads and writes through the BFF.**
`GET /bff/v1/notification-preferences` answers every topic and channel pair:
whether it opts in, whether it is required, defaulted or set, when it was set,
and its version, which is the id of its newest entry.
`PUT /bff/v1/notification-preferences/{topic}/{channel}` carries
`{ "optedIn": <bool> }` and a precondition: `If-Match` with the version it
read, or `If-None-Match: *` for a pair that has no entry yet. Notifications
appends only while the entry the precondition names is still the newest.
Where it is not, the request is a repeat when the newest entry holds the value
asked for and supersedes the entry named, or, for `If-None-Match: *`,
supersedes none, and it is answered as the first was.
Otherwise the precondition has failed, which §10.5 answers 412, with the
pair's present state in the body; the app shows that state and asks again.
The BFF refuses a request with no precondition, or with a topic or channel
outside the vocabulary, as malformed. Both routes make one synchronous call to
`Notifications.Api` over gRPC, carrying the caller's own access token, and
Notifications takes the subject from it, exactly as
[ADR-117](ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md)'s
registration does; the `PUT` is `Convergent`
([ADR-058](ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md))
inside that record's limit, because the interleaving the limit names cannot
land: a retry that arrives after a different write names an entry that write
has superseded.

**A send is decided when it is made.** The send pass reads the pair's newest
entry, or its default or requirement, when it makes the send and never when
the event arrives, so a withdrawal takes effect on the next send. The record
of every send names the entry it was decided under, or none when a default or
a requirement decided it.

**`PersonalDataDeleteRequested` anonymises the journal and keeps it.** Every
entry of the subject loses its subject, as an ended `NotificationLog` row loses
its `CustomerId`, and keeps its topic, channel, opt-in, times, client and the
entry it supersedes, and the statement stamps when it ran. It runs in the
transaction Notifications' erasure handler already runs, so the responder set
[ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)
fixes does not change.

**An entry stops being current** when a later entry supersedes it or an
erasure anonymises it, and **the retention pass deletes it once
`NotificationsJurisdictionOptions.ConsentRetention` has passed since then and
no record of a send still names it or the entry it superseded**. That window
is statutory, and the second condition keeps the entry a kept record of a
send was decided under, and the entry that ended it, for as long as that
record is kept, whenever the send ended. A current entry is never aged. That
delete is a further write the worker makes to a row the API wrote, beside
those ADR-118 names, and it is guarded as they are, by the row's own state: an
entry that is no longer current is never written by the API again.
**Why.** The owner's decisions on #474 set the substance. Their first,
on 2026-10-12, made the preference store the consent record and had promotional
classes opt in, off by default. Their calls of 2026-10-13 made it a journal
and had erasure anonymise it. A row overwritten on withdrawal cannot show what
the consent was when a send was made, which is the one question a consent
record exists to answer. Deleting the journal on erasure would destroy the
evidence that the sends made before it were lawful, while an entry with no
subject is rule 4's form: a record that holds nothing personal is kept as
evidence and not as a copy.

The key is a topic, not a class alone, as the issue's body recommended,
because the first decision lets a subject choose which events reach which
channel, and the decision outranks the body: a key of class and channel
cannot say "push me the dispatch but not the delivery". It is not an event for
everything either, because a promotional class has no domain event and the
consent that matters in law is the class's. Entries are written only when a
person acts, so the seven service topics cost no rows until somebody changes
one.

A requirement is a value and not a preference because a preference cannot
switch off a duty: ADR-053's table has the United Kingdom's confirmation given
on a durable medium, and a subject who turned that email off would put the
shop in breach. It is a deployment's value because rule 1 forbids a branch
that names a country.

The precondition is the entry read and not a sequence such as ADR-117's,
because a preference belongs to a subject who sets it from several devices,
and no counter is shared between them; the entry each device read is the only
order two devices have in common. Without it a stale opt-in landing after a
withdrawal is the interleaving ADR-058's limit names, and here it would send a
promotion to someone who had withdrawn. [§8.5](../08-caching-redis.md)'s key
would not do, for ADR-117's reason: it stops a repeat of one request, and not a
stale one landing after a different request.

A send names its entry because, after an erasure, that id is the only thing
left to show a send was lawful: the anonymised record of the send still names
the anonymised entry it was decided under. An entry is kept while a record
names it, and not for a window measured against the log's, for the reason
rule 4 keeps a template version until no record of a send names it: a send
can end after the entry it was decided under stopped being current, so no
fixed window outlives every record.
**Consequences.** **The journal grows with every act** and has no cap,
because a cap would delete evidence; the gateway's rate limit
([§10.3](../10-api-gateway.md)) is what bounds a client that writes in a loop.
**A send already being made when a withdrawal commits is still sent**: the pass
reads the entry before it calls the channel, so the residual is at most one
message for each notice the pass held at the moment of the withdrawal.
**In the reference deployment a subject cannot stop the order emails**, and the
preferences read shows them as required rather than leaving a switch that does
nothing. **An opt-in is not a delivery**: the operating system's permission
still decides whether a push is shown, and nothing here can see it. **A write
made after an erasure** appends an entry of the subject's own act, as ADR-117
accepts for a registration. Every record of a send now has a place for the
id of the entry it was decided under, empty where a default or a requirement
decided it, which the push amendment that follows on #474 inherits: each of
its delivery rows, one per device, names an entry or none.

**This is the channel consent and no other.** The lawful basis for holding a
customer's data at all, which ADR-053's table calls consent-centred in
Kazakhstan, and the realm's terms action, which ships disabled, remain what
ADR-053 and
[ADR-062](ADR-062-the-domain-is-a-reference-implementation.md) say an adopter
must bring. This record amends ADR-053's rule 1, whose list of what a
jurisdiction varies gains the required pairs, and its rule 4, whose consent
recorded nowhere is now recorded here, though a promotional template is still
an ADR before it is a pull request. It amends ADR-118, whose worker now makes
a further write to rows the API wrote.

**The pull request that builds it amends** what says otherwise, beside the
rows ADR-117 and ADR-118 owe:

| Owner | What it says today |
|---|---|
| [§10](../10-api-gateway.md) | No wire contract for the preferences routes beside the one ADR-117 owes for the devices route, and §10.5's 412 row naming `If-Match` and `If-Unmodified-Since` alone, with its close that the 412 half is unimplemented because nothing sends or reads an ETag |
| [Appendix E](../appendix-e-reference-domain.md) | E.1.1's Notifications row owning `NotificationLog` alone, E.6.9's Notifications row without the anonymised journal, and the configuration inventory without `Jurisdiction__Required` or `Jurisdiction__ConsentRetention` |
| `deploy/helm/` and `deploy/compose/` | No values for the two jurisdiction members, in the worker's chart or in Compose, nor in the `notifications-api` chart ADR-118 owes |
| `docs/personal-data.md` | No row for the journal |

**Amended by [ADR-123](ADR-123-a-send-is-decided-once-by-the-pass-that-commits-its-intent-and-a-resend-under-that-intent-is-not-decided-again-1041-474.md)**,
which adds to the residual after a withdrawal, beside the notices a pass
held, every send whose intent committed before it, until each ends: a resend
under a committed intent is not decided again.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
