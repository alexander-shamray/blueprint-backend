# ADR-032 — The saga's outbox is MassTransit's, in the saga's own transaction

**Decision.** [§9.6](../09-messaging.md)'s `ordering-fulfilment-saga` endpoint
takes MassTransit's Entity Framework outbox —
`AddEntityFrameworkOutbox<OrderingDbContext>` with
`UseEntityFrameworkOutbox<OrderingDbContext>(context)` on the endpoint — in
place of `UseInMemoryOutbox`. The bus-level call sets
`IsolationLevel = IsolationLevel.Serializable`, which is part of the mechanism
rather than a tuning knob: the outbox opens the consume transaction and the
saga repository joins it, so the level in force is this one and no longer the
`Serializable` the repository used to open for itself, and MassTransit's
default here is `RepeatableRead`. It brings three tables into the `ordering`
schema (`InboxState`, `OutboxState`, `OutboxMessage`), which is a **second
outbox table set** and therefore an exception to §9.3's prohibition on one. The
exception is this endpoint and no other; the platform's other three receive
endpoints keep the in-memory outbox, and every application-level integration
event still goes through §9.4's `ordering.OutboxMessages` and its dispatcher.

**Why.** [#128](https://github.com/alexander-shamray/blueprint-backend/issues/128)
is a dual write. `EntityFrameworkRepository` persists the saga instance and
`UseInMemoryOutbox` buffers the messages that transition sends, and the buffer
flushes **after** the repository has committed. A crash in that window leaves
the instance advanced and its commands never sent. Concretely, for a
`StockReserved` arriving in `AwaitingStock`: the instance moves to
`AwaitingPayment` and commits, `AuthorisePayment` and the `PaymentTimeout`
schedule are both still in the buffer, the process dies — and the order sits
in `AwaitingPayment` with stock reserved, no authorisation requested and **no
timeout to rescue it**, because the schedule was in the same buffer.

**The redelivery notices and cannot repair, which is the distinction this ADR
turns on.** §9.5's `InboxFilter` writes its row after the consumer returns, so
the same crash leaves no row and the message is redelivered — but the instance
has already moved on, so no transition accepts the event. `StockReserved` in
`AwaitingPayment` is not one of the machine's declared `Ignore`s (those sit
`During(Compensating)`), so it is genuinely unhandled: MassTransit's default
raises, §9.8's five retries are spent, and the message lands in the error queue
§13.6 pages on.

> **So the window was observable, and an earlier draft of this ADR said it was
> silent.** That draft followed [#128](https://github.com/alexander-shamray/blueprint-backend/issues/128)'s
> own text, which describes [#117](https://github.com/alexander-shamray/blueprint-backend/issues/117)
> as having replaced the fault with an `Ignore()` and a log line. #117 did try
> that and then **removed** it; what shipped keeps MassTransit's default and
> enumerates the legitimate arrivals individually. Reading the issue instead of
> the state machine is how a claim about a signal survived into a decision
> record about durability — the same failure as the API name two paragraphs
> on, and Copilot caught this one.
>
> **It does not weaken the case, it relocates it.** A page is not a repair.
> The error queue says an order is stranded; nothing in it recreates the
> `AuthorisePayment` that was never sent or the `PaymentTimeout` that would
> have rescued the order, so the outcome is an operator reading
> `docs/runbooks/error-queue.md` and reconstructing by hand what the process
> lost. What this decision buys is that there is nothing to reconstruct.

**§9.3's prohibition does not reach this case, and the sentence that said it
did rested on a premise now known to be false.** §9.3 forbids a second outbox
because "two dispatchers means two retention policies, two sets of ordering
guarantees, and one of them will be the one nobody monitors" — an argument
about a second **application** outbox competing with the first for the same
job. It then exempted sagas on the grounds that routing their output through
§9.4's outbox "would add a second staging hop with no additional guarantee".
That was true only while the in-memory outbox was believed to be durable. It is
not, and the alternative it dismissed is not merely more expensive — it is
**unavailable**: the saga's timeouts are scheduled messages, the delay is a
transport feature ([ADR-021](ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)),
and no dispatcher of ours can replay a delay it never held. An application
outbox carrying `AuthorisePayment` but not `PaymentTimeout` would close half
the window and leave the half with no bound at all.

> **The two mechanisms are not competing for one job, which is the whole of the
> exemption.** §9.4's outbox stages what a *handler* publishes, inside §6.3's
> transaction, and every one of Ordering's other three endpoints is durable
> that way already — their consumers publish through it, so the in-memory
> outbox there defers sends that have already committed. The saga is the one
> consumer that sends on the bus directly, and this is the outbox for that.
> Nothing is staged twice and no message can take either path.

**Both inboxes stay, and they answer different questions.** `InboxFilter` is
§9.5's long-window duplicate suppressor, pruned on §9.4's seven-day retention
— it is what stops a redelivered `OrderPlaced` starting a **second** workflow
hours after the first finalised, which is the defect PR-21 filed when §9.8's
saga exemption was removed. MassTransit's `InboxState` is a short-window
delivery record on its own duplicate-detection window, and it exists so the
outbox filter knows which of the committed messages it has already sent.
Retiring either one costs a guarantee the other never made.

> **The paragraph above cites §9.4 for the retention, and the citation
> should read §9.5; the sentence itself stands as written.** §9.4 keeps the
> outbox for a few days and names no figure; the window `InboxFilter` is
> pruned on is `RetentionPolicy`'s, and the purge that applies it is the one
> §9.5 specifies — so the two consequences below that call that purge §9.4's
> carry the same slip. An ADR is never rewritten, so a mis-citation is
> corrected beside its sentence rather than in it.

**Consequences.**

- **The `ordering` schema now holds five messaging tables where the chapters
  describe two.** MassTransit's are singular (`ordering.OutboxMessage`,
  `ordering.InboxState`, `ordering.OutboxState`) and this platform's are plural
  (`ordering.OutboxMessages`, `ordering.InboxMessages`). They do not collide,
  and that is a property of MassTransit's naming rather than a decision anybody
  took — a reader of the database sees five and should not have to work out
  which chapter owns which.
- **There are two retention policies, which is exactly what §9.3 warns
  about — and the second is narrower than "MassTransit's tables".**
  `AddEntityFrameworkOutbox` registers an
  `InboxCleanupService<OrderingDbContext>`, and the package's own documentation
  scopes it to one table: it "is responsible for removing `InboxState` entries
  after the expiration window timeout has elapsed". `ordering.OutboxMessage` is
  not on a timer at all — the outbox middleware removes a row once the message
  has reached the transport, so that table drains as a consequence of delivery.
  `ordering.OutboxState` is emptier still: it belongs to the bus-side outbox
  and, with `UseBusOutbox()` not called, **nothing writes, reads or prunes it**
  in this configuration. §9.4's `RetentionPurgeService` prunes ours and does
  not read any of the three. Folding the two together was
  considered and refused: deleting an `InboxState` row whose outbox messages
  have not been delivered turns a retention job into the message loss this
  decision exists to close. The cost is one more unmonitored timer, and it is
  taken rather than dodged.
- **`UseBusOutbox()` is deliberately not called.** It intercepts
  `IPublishEndpoint` and `ISendEndpointProvider` *outside* a consume context —
  the API request path, which §9.4's application outbox already owns. Calling
  it would put a third staging mechanism on a path that has no dual write.
  **The visible consequence is that `ordering.OutboxState` stays empty for
  ever**: it is the bus-side outbox's own table, so with that call absent
  nothing writes it, nothing reads it and nothing prunes it. It exists because
  `OutboxMessage.OutboxId` carries a foreign key to it and the model would not
  build without it — say so where an operator can read it, rather than leaving
  a permanently empty table to be diagnosed.
- **This is the first artefact in `Ordering.Infrastructure` whose EF model is
  not described by an `IEntityTypeConfiguration<T>` in this repository.** The
  three entities are MassTransit's, mapped by
  `modelBuilder.AddTransactionalOutboxEntities()`, so §7.2's "mapping lives in
  configuration classes" rule has one stated exception rather than a quiet one.
- **The scaffold is unaffected and that is not luck.** §4.5's scaffold reads
  Catalog as its template, and Catalog has no saga. A second service with one
  inherits this decision by citing it, not by copying a file.
- **The consume transaction is now open across a broker publish, which it was
  not before.** The outbox delivers inside the same transaction it committed
  in: begin, lock the `InboxState` row, save, **publish to RabbitMQ**, save,
  commit. Under `UseInMemoryOutbox` no database transaction was open while a
  send was in flight. So a degraded broker now holds a SQL transaction and a
  row lock for up to MassTransit's `MessageDeliveryTimeout` — 30 seconds at
  this pin — per staged message, per in-flight delivery. That is inherent to
  the mechanism rather than a defect in the wiring, and it is the price of the
  guarantee: the alternative to holding the transaction is not holding the
  guarantee. It belongs in the capacity argument for the connection pool, and
  in any runbook reading lock waits during a broker incident.
- **The path this decision exists to make survivable is also the one that
  leaks.** `InboxCleanupService` deletes `InboxState` rows whose `Delivered` is
  set. A transition that commits and then fails delivery past the endpoint's
  five retries leaves `Delivered` null for ever, so that row and the
  `OutboxMessage` rows beside it stay until somebody removes them by hand.
  §9.4's purge does not read those tables. The volume is bounded by how often a
  message exhausts its retries and reaches the error queue — which §13.6
  pages on, so the leak has an alarm in front of it though nothing measures the
  leak itself. Stated rather than closed: a cleanup that removed undelivered
  rows would be the message loss this decision is about, so the right owner is
  whoever drains the error queue.
- **A schedule survives the crash; its deadline does not survive it exactly.**
  The delay is carried as a *relative* value and re-applied when the outbox
  delivers, so a message staged and then delivered ten minutes later fires ten
  minutes late. On the ordinary path the drift is milliseconds. On the path
  this ADR is about — commit, crash, redelivery — a `PaymentTimeout` fires
  at 15 minutes plus the outage and a `DespatchTimeout` at three days plus it.
  Later is the safe direction for both, and "the schedule commits with the
  instance" should not be read as "the deadline is preserved".
- **`MessageDeliveryLimit` and `DuplicateDetectionWindow` are left at
  MassTransit's defaults, and both are worth knowing rather than tuning
  blind.** One message is delivered per transaction, so a transition staging
  two of them costs several sequential begin/commit cycles and inflates that
  row's `ReceiveCount` — which is therefore **not** a redelivery count, and a
  runbook reading it as one would be wrong. The duplicate-detection window is
  thirty minutes, which is what "short-window" means where this decision
  contrasts MassTransit's inbox with §9.5's seven-day retention. Neither is
  tuned here because nothing has measured what they should be; naming them is
  what stops the next reader assuming they were chosen.
- **The saga's consume transaction is longer, wider and stricter than it was,
  and §12.4's fixture is where that shows.** It now spans `InboxState`,
  `OutboxMessage` and the instance, at `Serializable`, and holds through a
  broker publish. Nothing in production deletes a schema, so nothing there
  meets it the way a test does — but Respawn's reset deletes every row in the
  `ordering` schema while a consumer from the previous test may still be
  committing, and two multi-table transactions taking locks in opposing order
  deadlock. That hazard predates this decision and the fixture already
  documented it; what this decision did was widen it, from a single-row write
  to a three-table `Serializable` one. `ServiceFixture.ResetAsync` retries
  error 1205 and carries the argument. **Recorded here rather than only there,
  because the branch tried to close it by removing a background service and
  the deadlock reproduced anyway** — a fixture retry answers a property of this
  decision, not a property of that service.
- **The pre-flush window stops existing; the catch-all does not come back.**
  #117 removed an `OnUnhandledEvent(x => x.Ignore())` because three arrivals
  reached it and it could not tell them apart — a post-flush duplicate, a
  pre-flush crash that had lost the instance's commands, and a misroute. This
  decision deletes the middle one, which leaves two, and a callback that
  answers two cases the same way is still only as right as its worse one. The
  enumeration stays, and so does the default that faults everything it does
  not name.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
