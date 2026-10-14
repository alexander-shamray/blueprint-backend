# ADR-118 — Notifications gains an API host for device registration and preferences, and the push adapters stay in the worker (#474)

**Decision.** Notifications gains a second host, `Notifications.Api`, beside
`Notifications.Worker` and `Notifications.Migrator`. It serves the
synchronous half of the service and nothing else: the device registration
method
[ADR-117](ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md)
left to this decision, and the per-subject channel preferences #474 owes
next, both called by the BFF over gRPC on an HTTP/2-only port that no gateway
route matches. It consumes no message, publishes none, runs no background
loop and calls no third party. **The worker keeps everything asynchronous**:
the consumers, the send worker, the erasure handler, the retention pass, and
the push adapters beside the mail channel, each provider behind its own hop
as the mail relay is.

**The two hosts are one service and share its one database**, each composing
the one Infrastructure project from a `Program.cs` of its own. The API writes
the device rows and the preference journal. The worker reads both at send
time and writes the device rows in three places ADR-117 names — its retention
pass deletes a row past the stale window, a provider's unregistered answer
deletes the row still holding the token the send used, and the erasure
handler clears the subject's rows — and anonymises the journal on an erasure.
Each of those writes is one statement or one transaction guarded by the row's
own state — the retention delete by the row's last-seen time, the unregistered
delete by the token the send used, the erasure by the subject — so a write
from the other process lands before it or after it and never between; the
database orders them, not a lock between the hosts.

The schema stays the migrator's, run by the worker's chart as it is today;
**the API host's chart renders no migration Job**, so two hooks never race over
one schema, and a rollout deploys the worker's chart before the API's, so the
API never starts against a schema it is ahead of.

The API host is its own workload, `notifications-api`, in a chart of its own.
**It is autoscaled, as `catalog-api` is**
([§15](../15-cicd-deployment.md)), because a person waits on its requests and
it waits on its database and on nothing a third party paces. That amends
[ADR-063](ADR-063-a-worker-that-waits-on-a-third-party-is-scaled-by-hand.md),
whose decision that Notifications' charts run no autoscaler now holds of the
worker's chart alone. **Switching Notifications off under
[ADR-104](ADR-104-an-operator-may-switch-off-notifications-shipping-and-privacy-for-a-bound-and-no-other-service.md)
suspends the worker and not the API host**, whose chart has no row of that
record's and so may not take `suspended`: mail and pushes wait, and
registrations and preference writes go on. **This record amends
[ADR-065](ADR-065-every-workload-is-fenced-by-a-default-deny-networkpolicy.md)'s
table** with the row ADR-117 left unnamed: `notifications-api` admits
`web-bff` on `grpc` and reaches its stores, and `web-bff` reaches
`notifications-api` on `grpc`. The worker's row is unchanged: it admits
nothing.
**Why.** ADR-117 tied the registration method's host to the adapters' host,
because the two are one question, and the question is whether the
synchronous half and the asynchronous half of Notifications share a process.
Serving the method from the worker would deploy one thing fewer and costs
three things that matter more. The worker is sized by hand for a mail relay's
pace and, once this lands, two push providers'; a request a person is waiting
on would ride on a replica count chosen for someone else's rate limit. A send
outage — a provider down, a backlog the worker is chewing through, the worker
switched off — would take registration with it, though registration needs
nothing a send needs. And ADR-104 lets Notifications be switched off on the
ground that it has no caller, which ADR-117 already had to amend once; with
the method in the worker, every suspension would fail every registration. A
host of its own keeps ADR-104's ground true for the part that is switched off
and puts the part with a caller where request-serving hosts are.

That departs from five statements, and says so. [§4.1](../04-solution-structure.md)
draws one `<Service>.Api/Program.cs` as the only composition root, or a
`<Service>.Worker` in its place; Notifications has both, each the only root of
its own process and each composing the same Infrastructure project, which no
service has done before — the migrator, which §4.2 keeps from being a root at
all, is no precedent. §4.1 and §15.2 count two
images per service, a host and a migrator; Notifications builds three, because
a second process is a second image. §15.3 gives each service a chart and puts
the migration hook in it so that one deployable rolls alone and still
migrates; Notifications has two charts and the API's cannot roll alone across
a migration, which the rollout order answers instead of a second hook. And
the render checks hold that a chart has a migration Job exactly when it owns a
database; `notifications-api` owns one and runs no Job, a third class beside
the two those checks know. The alternative to all five — the method in the
worker — costs what the paragraph above names, every day, where these cost a
deploy rule.

The preferences go with the registration because they are the same shape of
work — a BFF-driven write for one subject, answered while the person waits —
and a third host for them would split one kind of thing in two. The adapters
stay with the sends because a send is the only thing that calls them, from
the row the worker already leases.
**Consequences.** **Notifications stops being a pure consumer.** It now has a
host a caller reaches, and the scaffold's `--pure-consumer` shape no longer
describes it; it is a service with a worker and an API host and no Domain
project, a shape the scaffold does not render. **Another chart, another image
and another smoke target**, each a place a later edit can drift, and an
ADR-065 row that is a second statement of the edge the BFF's client already
names. **The rollout order is now load-bearing**: an API host deployed before
its worker's migration runs against the old schema until it lands, so the
order is a rule the rollout holds rather than a convention. **Two processes
write one schema**, which the platform has not had before; what keeps that
safe is that every write the worker makes to a row the API wrote is guarded
by that row's own state, so a change to one of ADR-117's guards is a change to
this record's safety too.

ADR-117's closing table assumed the worker might host the method, and its
rows for [§15.3](../15-cicd-deployment.md), `deploy/helm/smoke.sh` and
`deploy/helm/notifications/values.yaml` are void: the worker keeps no
Service. **The pull request that builds it amends** these besides ADR-117's
other rows:

| Owner | What it says today |
|---|---|
| [§4.1](../04-solution-structure.md) | One `<Service>.Api/Program.cs` as the only composition root or a `<Service>.Worker` in its place, two images per service, and no `Notifications.Api` project in the tree |
| [§15](../15-cicd-deployment.md) | §15.2's two images per deployable, §15.3's chart per service with a hook that lets one deployable roll alone and migrate, no rollout order between two charts of one service, and Notifications among the hand-scaled workers alone |
| [Appendix E](../appendix-e-reference-domain.md) | E.2.1's host shapes, which give Notifications the pure-consumer shape |
| `deploy/helm/` and `deploy/canary/deployables/` | No `notifications-api` chart, and no descriptor naming it, without which the smoke check that every chart on disk has a descriptor fails; and `smoke.sh`'s two classes on `smoke.migrator`, a chart that migrates its own database or one that has none, with no class for a chart that has a database and no Job — **asserted**: the render checks go red |
| `deploy/compose/` and the CI image matrix | No unit and no image for the API host |

**Amended by
[ADR-120](ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md)**,
the preference journal, whose retention pass is a further write the worker
makes to rows the API wrote, beside the device writes and the journal's
anonymisation named above. It deletes an entry that is no longer current,
which the API never writes again, so it is guarded by the row's own state as
they are.

**Amended by
[ADR-122](ADR-122-notifications-two-hosts-roll-in-lockstep-so-a-migrations-previous-release-is-the-one-both-hosts-run-1038.md)**,
which bounds the API host's rollout from below as well as from above, and the
worker's by the API host, so the two hosts roll in lockstep and a migration's
previous release is the one both run.

**Amended by [ADR-124](ADR-124-the-notifications-worker-reaches-each-push-provider-an-app-names-on-the-port-its-address-dials-474.md)**,
which gives the worker's row the edge its push adapters need: it reaches each
push provider an app names, and still admits nothing.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
