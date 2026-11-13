# ADR-205 — The assistant release's memory limit holds ADR-204's 256 callers at their worst, amending ADR-204 (#1980)

**Decision.** The `web-bff-assistant` release's `resources` in
`deploy/helm/web-bff-assistant/values.yaml` request 512Mi and limit memory to
3Gi, sized for `AssistantOptions.MaxCallers` callers each holding
`AssistantLimits.MaxConversations` conversations at
`AssistantLimits.ConversationTokenBudget`, about four characters a token held
as UTF-16 text. The chart's comment states that arithmetic, and a change to
any of the three limits revisits the figure.
**Why.**
[ADR-204](ADR-204-the-bffs-assistant-caps-daily-tokens-bounds-event-streams-and-sizes-its-caller-limit-1970-1972.md)
gave the BFF 256 callers where the core's default is 64, and recorded that the
release's 512Mi had been sized for fewer conversations than that at their
worst. At the worst, 256 callers of 8 conversations at 150,000 tokens come to
about 2.5 GB of text, so a replica that reached it was killed by the node, and
[ADR-153](ADR-153-the-deployed-bff-runs-the-assistant-on-a-single-replica-release-behind-its-own-gateway-route-1451-amending-adr-151-and-adr-065.md)'s
one replica restarted with every customer's conversations and the day's spend
counts gone.
**Consequences.** The figure is arithmetic and not a measurement: no load was
sent to a running replica, so the runtime's own use beside the text, and what
a conversation holds besides its messages, are covered by the margin to 3Gi
and not by a reading. A node must have 512Mi free to place the pod and may
take the pod past it up to 3Gi, so the node can be overcommitted by this one
release. The limit is a ceiling for a case ADR-204's per-customer daily
budget makes unlikely, at roughly four megabytes of text an account a day,
and is paid for in every cluster the release runs in. Bounding a caller's held
history by bytes, which holds the replica at a figure whatever the limits
say, is not decided here. ADR-204 is otherwise unchanged and is read beside
this record.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
