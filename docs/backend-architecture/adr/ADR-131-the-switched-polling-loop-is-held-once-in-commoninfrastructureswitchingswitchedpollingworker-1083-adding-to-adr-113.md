# ADR-131 — The switched polling loop is held once in Common.Infrastructure.Switching.SwitchedPollingWorker (#1083), adding to ADR-113

**Decision.** `SwitchedPollingWorker` is an abstract `BackgroundService` in
`Common.Infrastructure.Switching`, and every `ISwitchedWorker` that polls on a
timer derives from it. The base owns the `PeriodicTimer`, the skip of a tick
whose `SwitchName` is listed off (ADR-113), the catch of a failed pass while
the host is running, and an optional drain: a worker that sets `StopDrain`
gives its rows a token that fires that long after a stop. A worker supplies
its interval, its pass, whether a pass runs before the first tick, and what a
failed pass logs. A loop that does not poll on a timer implements
`ISwitchedWorker` directly, as ADR-113 allows.
**Why.** Nine workers wrote the loop themselves, in three shapes that nothing
said were deliberate: with and without a stop test before the tick, and a
`do` loop for the passes at start. A change to what a switched-off tick or a
failed pass does was nine edits across four assemblies.
**Consequences.** The catch is the token test in every worker, as eight
already had it. `OverdueSweepService` caught anything but
`OperationCanceledException`, so a deadline's own cancellation now logs and
ticks again where it used to stop the host; a stop still ends the loop, and a
pass that throws while a stop is under way now escapes it as in the other
eight, where the sweep used to log it. A
stop that finds no pass under way disposes the drain source before its budget
runs, as the two senders always did, because there is nothing to drain. The
loop is `sealed` in the base, so a worker that needs another shape derives
nothing here and takes ADR-113's interface by itself. The base is public API
of a building block, pinned by `SwitchedPollingWorkerTests` in
`Common.Infrastructure.Tests`.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
