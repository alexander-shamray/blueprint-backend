# ADR-206 — The core ends a turn whose client tool calls time out in two rounds running (end reason error, error type client-unresponsive), and a provider failure's message is withheld from the client unless AssistantLimits.ErrorDetail is on, the BFF logging the failure's type from TurnSummary.Failure (#1974, #1981), amending ADR-204 and ADR-151; Common.Assistant 0.2.0 to 0.3.0

**Decision.** `AssistantSession` ends a turn whose client tool calls time out
in two rounds running: it emits an error event of type `client-unresponsive`,
status 408 and no detail, and the turn ends with the existing end reason
`error`. A round with no timeout resets the count. `AssistantLimits` gains
`ErrorDetail`, off by default: off, the `provider-error` event carries its type
and title alone; on, it carries the redacted message as before. `TurnSummary`
gains `Failure`, the type name of the exception that ended the turn, null
otherwise, and the BFF logs it at warning with the conversation id. The BFF
leaves `ErrorDetail` off; a host whose one caller is an operator turns it on.
`Common.Assistant` moves from 0.2.0 to 0.3.0, as
[ADR-150](ADR-150-the-assistant-packages-version-rule-is-checked-on-the-pull-request-and-their-blocks-leave-the-shared-filter-1447-amending-adr-149.md)
asks of a change to it.
**Why.** The redactor is
[ADR-151](ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-1347.md)'s
data policy, not a filter for what a failure says, so a customer could read the
provider's own error text, a model name or a proxy's address. And a client that
never answered its tool calls kept a turn running for `ClientToolTimeout` a
round, which kept its caller from being evicted; [ADR-204](ADR-204-the-bffs-assistant-caps-daily-tokens-bounds-event-streams-and-sizes-its-caller-limit-1970-1972.md)
left ending it early undecided.
**Consequences.** A host that relied on the detail now sees none until it sets
`ErrorDetail`, so the default is a behaviour change and the minor version
carries it. The host learns the failure's type and not its message, so a
diagnosis that needs the text turns the setting on for a run. Two timeouts in
a row end a turn whose client was merely slow twice, and the customer sends
the message again; a client answering inside the timeout is unaffected. The
new error type is one a client written before it has no words for, and shows
the title.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
