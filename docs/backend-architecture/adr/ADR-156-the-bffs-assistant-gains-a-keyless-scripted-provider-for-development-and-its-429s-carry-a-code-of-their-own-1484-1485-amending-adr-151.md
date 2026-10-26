# ADR-156 — The BFF's assistant gains a keyless scripted provider for Development, and its 429s carry a code of their own (#1484, #1485), amending ADR-151

**Decision.** `Assistant:Provider` takes a third value, `scripted`, which
builds `ScriptedReplyProvider`: it calls nothing and needs no key, reports
itself available, and answers every turn with one fixed reply, streamed a word
at a time, with no tool call. `AssistantOptions.ProviderInEffect` refuses it,
naming the key and the environment, in any host environment but Development,
so the start fails rather than a real customer meeting a canned answer.
Compose's BFF unit sets it, so the local stack, and a client's end-to-end job
that starts that stack, reaches the assistant with no provider account. Each
429 the BFF's assistant answers carries a `code`, the house marker of
[§10.5](../10-api-gateway.md#105-error-responses): `assistant.rate_limited`
when `Assistant:TurnsPerMinute`'s limiter refuses, written through
`IProblemDetailsService` as the gateway's rejection is (§10.3), and
`assistant.conversations_full` when the store cannot make room: every
conversation the caller may hold, `AssistantOptions.MaxConversations`, is
running a turn, or the replica holds `MaxCallers` callers and none is idle.
They are `AssistantEndpoints.RateLimitedCode` and
`AssistantEndpoints.ConversationsFullCode`. Neither carries `Retry-After`, as
before.
**Why.** The Compose stack left the provider unset, so the assistant was off
and the status route said so: the web client renders nothing of it then, and
its panel met the host it is written for only in unit specs against a stub.
`anthropic` would put a paid key in every client's CI. A provider of the
core's own interface is the seam
[ADR-149](ADR-149-the-ai-assistants-core-and-its-adapter-are-two-building-blocks-this-repository-publishes-as-nuget-packages-1346.md)
already draws, so the scripted one exercises the host's routes, limiter,
redactor and stream unchanged, and only the model is absent. Development is
the one environment Compose and `Web.Bff.Tests` run in; the platform names no
separate test environment, and adding one for this guard would be a second
lever with no other reader. A client also had no reliable way to tell the
BFF's 429 from the gateway's: it read a missing `Retry-After` as the BFF's,
and the gateway's 429 also arrives without one behind an intermediary that
strips it, with CORS off, and from a gateway older than `27b54a7`, so a
gateway refusal of an assistant request opened no back-off window. `code` is
where §10.5 already tells a client to switch; a header of the BFF's own would
be a second mechanism, and a problem `type` a URI the platform mints nowhere
else.
**Consequences.** The Compose assistant answers with a fixed reply, not a
model: a developer who wants the real one overrides `Assistant__Provider`
to `anthropic` and supplies `ANTHROPIC_API_KEY` in a local override, which no
committed file carries. Two guards keep `scripted` out of a deployment: the
host's environment check, and §15.4's chart, which still renders the key only
for `web-bff-assistant` and accepts `anthropic` alone. A turn's log names
`scripted` as both provider and model. The two codes join §10.5's closed
vocabulary, and the gateway's 429 still carries none, so a client tells the
two apart by the code's presence rather than by a header's absence; the web
client's half, keying its exemption on the code and adding an end-to-end spec
of the panel, stays on blueprint-frontend#350.
[ADR-151](ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-off-by-default-and-scoped-to-the-caller-1347-amending-adr-053-adr-149-and-adr-150.md)
is otherwise unchanged and is read beside this record.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
