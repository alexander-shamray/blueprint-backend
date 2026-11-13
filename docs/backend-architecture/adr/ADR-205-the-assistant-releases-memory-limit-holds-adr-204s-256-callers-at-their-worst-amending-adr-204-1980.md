# ADR-205 — The assistant release's memory limit holds ADR-204's 256 callers at their worst, amending ADR-204 (#1980)

**Decision.** The `web-bff-assistant` release's `resources` in
`deploy/helm/web-bff-assistant/values.yaml` request 512Mi and limit memory to
4Gi, sized for `AssistantOptions.MaxCallers` callers each holding
`AssistantLimits.MaxConversations` conversations whose history has reached
`AssistantLimits.ConversationTokenBudget`, about four characters a token held
as UTF-16 text. The chart's comment states that arithmetic, and a change to
any of the three limits revisits the figure.
**Why.**
[ADR-204](ADR-204-the-bffs-assistant-caps-daily-tokens-bounds-event-streams-and-sizes-its-caller-limit-1970-1972.md)
gave the BFF 256 callers where the core's default is 64, and recorded that the
release's 512Mi had been sized for fewer conversations than that at their
worst. 256 callers of 8 conversations at 150,000 tokens come to about 2.3 GiB
of history. The runtime caps its managed heap at 75% of a container's memory
limit unless told otherwise, and nothing here tells it, so 3Gi would have
thrown out of memory at 2.25 GiB of heap, short of the figure, and 4Gi leaves
3 GiB. A replica that ran out of memory restarted, and
[ADR-153](ADR-153-the-deployed-bff-runs-the-assistant-on-a-single-replica-release-behind-its-own-gateway-route-1451-amending-adr-151-and-adr-065.md)'s
one replica took every customer's conversations and the day's spend counts
with it.
**Consequences.** The figure is arithmetic and not a measurement: no load was
sent to a running replica. It is also not a bound. A turn is appended to a
conversation before the context budget is read, so a conversation at the
budget takes another message of up to `AssistantEndpoints.MaxTextLength`
characters on every turn, which sends nothing to the provider and spends no
daily token; and the session keeps each message and reply a second time for
the conversation read. The 0.7 GiB beside the history covers that second
copy at about a third of the history and the runtime, and nothing more. A node
must have 512Mi free to place the pod and may take the pod past it up to 4Gi,
so the node can be overcommitted by this one release, in every cluster the
release runs in. Bounding a caller's held history by bytes, which would hold
the replica at a figure whatever the limits say, is not decided here and
stays open in #1980. ADR-204's daily budget bounds the tokens an account
spends, roughly four million characters, 8 MB held as UTF-16, in a day, and
does not bound what its conversations hold. ADR-204 is otherwise unchanged and
is read beside this record.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
