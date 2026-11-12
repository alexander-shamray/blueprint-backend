# ADR-204 — The BFF's assistant caps each customer's and the host's daily tokens, bounds a customer's event streams, and sizes its own caller limit (#1970, #1972), amending ADR-151 and ADR-156

**Decision.** The BFF's assistant refuses new work, opening a conversation or
starting a turn, once a daily token budget is spent:
`AssistantOptions.DailyTokensPerCustomer` for one customer, and
`AssistantOptions.DailyTokensForHost` for every customer together.
`AssistantSpend` adds each turn's input, output and cached tokens as the turn
ends, read from the core's `TurnSummary`, which now carries the turn's `Caller`
and `TurnSummary.Unreported`, the core's estimate for a round cut short before
the provider reported its usage, and forgets every count on a timer within
`AssistantSpend.SweepPeriod` of the UTC day turning, as
[ADR-152](ADR-152-the-assistant-core-sweeps-idle-conversations-on-a-timer-so-idletimeout-plus-a-quarter-bounds-retention-1457-amending-adr-151.md)'s
sweep bounds a conversation. The refusal is a 429 whose `code` is
`AssistantEndpoints.DailyLimitCode`
([§10.5](../10-api-gateway.md#105-error-responses)), with no `Retry-After`, as
the host's other 429s. The `…/events` route takes a limiter policy of its own,
`AssistantEndpoints.StreamPolicy`: a concurrency limit per subject of
`AssistantOptions.StreamsPerCustomer`, whose refusal carries
`AssistantEndpoints.RateLimitedCode`. `AssistantOptions.MaxCallers` takes a
default of this host's, where the core's `AssistantLimits.MaxCallers` stays for
a host with one caller. `Common.Assistant` ships under a new minor `Version`, as
[ADR-150](ADR-150-the-assistant-packages-version-rule-is-checked-on-the-pull-request-and-their-blocks-leave-the-shared-filter-1447-amending-adr-149.md)
requires, since `TurnSummary` gains a parameter.
**Why.**
[ADR-151](ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-1347.md)
made the limit on what a customer can spend the host's, and its one limit was
the start rate, which bounds starts a minute and not spend over a day: a turn
runs up to `AssistantLimits.MaxSteps` rounds,
`AssistantLimits.ConversationTokenBudget` bounds a conversation's context and
not its spend, and a new conversation starts from nothing. A customer's budget
bounds one account; only a budget for the host bounds many accounts, which no
per-account cap can. `TurnSummary` is the seam the core already gives a host for
a turn's usage, and
[ADR-153](ADR-153-the-deployed-bff-runs-the-assistant-on-a-single-replica-release-behind-its-own-gateway-route-1451-amending-adr-151-and-adr-065.md)'s
single replica lets the counts live in memory with no store. The core's caller
limit suits the admin console's one caller: on a host with customers, a new
customer past it evicted another customer's conversations, and as many running
turns as it allows refused every other customer. The event stream was outside
the start limiter, so one customer could hold any number of requests open, each
for up to `AssistantEndpoints.StreamLifetime`.
**Consequences.** A turn is counted when it ends, so turns running when a budget
is reached finish past it: the cap bounds what a customer starts, and the
overshoot is at most the turns they have running, each bounded as above. The
host counts a turn just after its conversation reads idle, so a turn started in
that moment is not refused. Cached input counts as whole tokens, so the count
reads above the provider's bill. A provider reports a round's usage only as it
ends, so a round a customer cancels, or a stream that breaks, is counted at the
core's estimate, its input from the size of what was sent and its output at
`AssistantLimits.MaxOutputTokens`: starting and cancelling turns spends the
budget too, and a cancel costs a customer the round's bound. The client's
`turn_ended` event still carries only what the provider reported. A restart,
which every rollout is under the release's `Recreate`, forgets the day's counts,
as it forgets the conversations. Once the host's budget is spent the assistant
refuses every customer until midnight UTC, so enough accounts can deny it to
everyone for the rest of the day: availability is traded for a bounded bill on
purpose. The counts are keyed by the customer's id, which
`docs/personal-data.md` records. A larger caller limit lets the release's one
replica hold more conversations than its chart's memory limit was sized for at
their worst, each at the context budget. A turn parked on a client tool call the
client never answers still holds its caller's place for
`AssistantLimits.ClientToolTimeout` a round, and ending it early is not decided
here. The stream's refusal shares the limiter's code, since a client backs off
from both alike.
[ADR-151](ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-1347.md)
and
[ADR-156](ADR-156-the-bffs-assistant-gains-a-keyless-scripted-provider-for-development-1484-1485.md)
are otherwise unchanged and are read beside this record.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
