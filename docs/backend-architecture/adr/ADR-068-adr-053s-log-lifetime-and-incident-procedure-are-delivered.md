# ADR-068 — ADR-053's log lifetime and incident procedure are delivered

**Decision.** Two of the items
[ADR-053](ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)'s
consequences call owed are discharged, and by these and nothing else: the
lifetime of the log store is the one
[§13.4](../13-observability.md#how-long-a-log-and-a-trace-are-kept) states as
a value each deployment sets in its log and trace stores' own retention, and
the procedure for a personal-data incident is
[`docs/personal-data-incident.md`](../../personal-data-incident.md), outside
`docs/runbooks/` where ADR-053 placed it. The minor-unit table is
[ADR-067](ADR-067-a-currencys-minor-unit-is-iso-4217s-held-once.md)'s, and
every other item ADR-053 names stays owed.
**Why.** ADRs are append-only, so ADR-053 cannot be edited to say its debts
were paid, and a reader of it alone still sees both as open. A record that
names what discharged each item is the one place that reader is sent, and
saying which items it does not discharge keeps the remainder visible rather
than implying the list is settled.
**Consequences.** ADR-053's consequences now read correctly only beside this
record and ADR-067, so a third discharge adds a third record rather than
amending either. A lifetime stated as a deployment value is delivered as a
rule and not as a number: a deployment that sets none still keeps logs
indefinitely, and §13.4's readiness sentence, not this record, is what refuses
it customer traffic.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
