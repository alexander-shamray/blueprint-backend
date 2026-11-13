# ADR-208 — The assistant core bounds each caller's held history by characters (AssistantLimits.CallerHistoryChars), forgetting the caller's least recently used idle conversation and otherwise ending the turn with budget, and a turn past a budget holds none of its text, giving ADR-205's 4Gi a bound to hold (#1980), amending ADR-205; Common.Assistant 0.3.0 to 0.4.0

**Decision.** `AssistantLimits` gains `CallerHistoryChars`, the history
budget, 2,000,000 by default: the characters one caller's conversations may
hold together, each history message as the provider is sent it and the
caller's text and the replies again as the conversation read holds them.
Before a turn starts and before each of its rounds, the store forgets the
caller's least recently used idle conversations, never the one the turn is in,
while the caller is at the history budget, and forgets none when that one alone
is at it; a caller still at it ends the turn with the existing end reason
`budget`. A conversation is checked idle and closed in one hold of its lock, so
a turn that starts between the two is not cancelled by the eviction. A turn
that would start past the history budget or the context budget,
`AssistantLimits.ConversationTokenBudget`,
holds none of its text, sends nothing to the provider and ends with `budget` at
once, and a second such turn adds nothing to the conversation read.
`AssistantOptions` carries the setting and refuses a value that is not
positive. `Common.Assistant` moves from 0.3.0 to 0.4.0, as
[ADR-150](ADR-150-the-assistant-packages-version-rule-is-checked-on-the-pull-request-and-their-blocks-leave-the-shared-filter-1447-amending-adr-149.md)
asks of a change to it.
**Why.**
[ADR-205](ADR-205-the-assistant-releases-memory-limit-holds-adr-204s-256-callers-at-their-worst-amending-adr-204-1980.md)
sized the `web-bff-assistant` release at 4Gi by arithmetic and recorded that
the figure was not a bound: a turn was appended before the context budget was
read, so a conversation at the context budget took another message on every
turn, and nothing limited what a caller held but the count of their
conversations. A replica that ran out of memory took every customer's
conversations and the day's spend counts with it. Counted per caller, the
store holds at most `AssistantOptions.MaxCallers` times the history budget,
256 times 2,000,000 characters, about 0.95 GiB as UTF-16, beside, for each
conversation, its last turn's opening message, which
`AssistantEndpoints.MaxBodyBytes` bounds on arrival, and the round after that
turn's last check, which `AssistantLimits.MaxOutputTokens` and
`AssistantLimits.MaxToolResultChars` bound per call. That leaves about 2 GiB of
the 3 GiB heap ADR-205's limit gives for what the count leaves out, and a
change to `MaxCallers` or `CallerHistoryChars` is what revisits the 4Gi. The
default holds two or three conversations at the context budget, as more or
less of each is the caller's text and replies, which the read holds again.
**Consequences.** The count is characters, not the bytes the runtime spends
on them: the objects around each string, a tool item's label, the event ring
and the runtime itself are the 2 GiB's to cover, and no load was sent to a
running replica, so 4Gi is still not a measurement. A round's reply is counted
as it arrives and its history message when it ends, but either is weighed
against the history budget only before the next round starts, so a caller
running every conversation at once may pass it by that many rounds, and the
excess stays until the store forgets those conversations. A conversation's
own turns make no room for it, so one at the history budget alone goes only by
another conversation's turn, `AssistantLimits.IdleTimeout`, a delete, or the
store's conversation or caller limit. A customer
past the history budget loses their oldest idle conversation before
`AssistantLimits.IdleTimeout` would have ended it, and one conversation that
reaches the whole history budget alone ends every turn with `budget`, so the
customer starts another. The message of a turn refused at its start is not in
the conversation read, so a client that showed it before the reply must drop
it on `budget`. The store sums the caller's conversations before every round,
at most `AssistantLimits.MaxConversations` of them. A host that relied on a
conversation at the context budget still taking messages now sees each
refused, and the minor version carries it. ADR-205 is otherwise unchanged and
is read beside this record.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
