# ADR-054 — A shipment stops waiting on its carrier at an age

**Decision.** Every wait a shipment has on its carrier ends, and each ends
with an outcome a person can read off the row.
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
ended the first one, a `Pending` shipment's; this record ends the other two
and gives each worker a failure count of its own.

1. **A cancellation the carrier does not answer ends at the give-up age.** A
   `Booked` shipment whose cancellation `ICarrierGateway.CancelAsync` keeps
   failing is retried on the backoff until `FulfilmentOptions.GiveUpAge` has
   passed since `CancellationRequestedAt`. The next pass then stamps
   `CancellationRefusedAt` without asking again, logs that it gave up, and the
   row leaves the fulfilment claim. The silence is recorded as the refusal
   because what follows is the refusal's: the parcel may be moving, tracking
   goes on, `ShipmentDispatched` is published when it is collected, and the
   saga's `CancelledAfterConfirmation` review row is the record. The column
   says the carrier will not cancel; the log line says whether it answered.
   It is the same age and not a second setting, because it is the
   deployment's one patience with a fulfilment pass that gets no answer, and
   a second key is a value every chart must supply for a choice nobody has
   asked to make apart from the first.
2. **A shipment the carrier never finishes ends at `TrackingWorker.GiveUpAge`,
   measured from `CreatedAt`.** A `Booked` or `Dispatched` shipment still not
   terminal at that age — a parcel lost, returned or never collected — is
   **`Abandoned`**: terminal, `TerminalAt` stamped, no longer polled, and
   nothing published. `TerminalAt` is where
   [ADR-053](ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)'s
   address window starts, so that address is deleted as any terminal
   shipment's is. Measured from creation and not from the carrier's last
   event, because a feed that goes on reporting a parcel in transit would
   otherwise hold an address for as long as it talks, and an outer bound is
   what the address needs. Both ages count from `CreatedAt`, so
   `FulfilmentOptions` refuses a give-up age at or past this one at start:
   a shipment booked that late would be abandoned on its first poll.
3. **Each worker counts its own failed passes.** `Attempts` is the fulfilment
   pass's and `PollAttempts` the tracking pass's, and a pass that succeeds
   clears only its own. One shared column let a working events feed reset the
   ladder of a cancellation that kept failing, so the cancel was retried at
   the ladder's first steps for as long as the outage lasted.

**Why.** ADR-052 calls a row retried for ever "a defect nobody is shown", and
two were left: a `Booked` row whose cancellation fails, retried for as long as
the row exists, and a `Booked` or `Dispatched` row the carrier never reports
delivered, polled for as long as it exists. The second is also a retention
obligation silently unmet — the address window starts at a terminal state, so
a shipment that never reaches one keeps the one personal datum this service
holds for ever, and nothing signals it.

The tracking age is a constant in code rather than a value of the deployment,
unlike ADR-052's. That age is sized to another service's deadline, and an
operator riding out a long outage decides whether a day's shipments wait for
it. This one is a ceiling past any carrier's delivery, and nothing on the
platform waits on Shipping that long: the saga has finalised on the despatch
or raised the order for review at `OrderFulfilmentSaga.DespatchTimeoutDelay`.
Set too long, it delays the address's deletion by the difference; set too
short, it stops following a parcel still moving, and its delivery is never
published.

**Consequences.** **A parcel delivered after its give-up age is delivered
unseen**: `ShipmentDelivered` is never published and Notifications sends
nothing. The age is set far enough out that this is a parcel somebody has
already asked about.

**A refused cancellation and an unanswered one look the same in the table.**
An operator separates them by the worker's log, and the saga's review row is
the same for both.

**`Abandoned` joins the terminal states**, and every surface that is read
from the enum — the waiting gauge's `state` tag among them — reports it
without a change.

**The shipments table gains a column**, and each claim a filtered index that
leaves the terminal rows out, since the table is never purged and each empty
tick of either worker would otherwise scan it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
