# ADR-151 — The Web BFF hosts the AI assistant for signed-in customers, off by default and scoped to the caller (#1347), amending ADR-053, ADR-149 and ADR-150

**Decision.** `Web.Bff` references `Common.Assistant` and
`Common.Assistant.Anthropic` and serves the AI assistant design's §5.1
protocol at `/v1/assistant`, behind its own authentication: the core's
`Caller` is the token's subject, so a conversation is visible to that
customer alone and another's id answers 404. It is off unless
`Assistant:Provider` names `anthropic`; off builds no provider, resolves no
credential and maps only the status route. Its tools are
`CustomerReadTools`, under the core's read-only `ToolPolicy`: `list_orders`
and `get_order` read
[ADR-051](ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)'s
projection keyed by the caller's subject, and `get_product` asks Catalog's
pricing hop under this host's client credentials, as the quote does. None
carries the customer's token. **The data policy**: the provider is shown the
customer's messages after `CustomerRedactor` has replaced email addresses,
international phone numbers and card numbers that pass the Luhn check, and
what the tools return — the caller's own orders' ids, statuses, timelines,
product names, quantities, amounts, payment and refund amounts and tracking
numbers, and a product's name and price. It is never shown the id, name,
mailbox, address or token the platform holds for the customer, whose own text
reaches it only after redaction, and a tool's failure reaches it as a code,
never as an exception's text. The provider's client is the SDK's own, whose
credential is the `ANTHROPIC_API_KEY` a deployment sets from its secret store;
no `Assistant` key may hold one, and the host refuses to start on a key named
like one. The core's limits are `AssistantOptions`, and
`Assistant:TurnsPerMinute` is a fixed window per subject on the two routes
that start work, opening a conversation and starting a turn: a limiter of
this host's own, and every route takes a body of at most
`AssistantEndpoints.MaxBodyBytes`, so nothing a client sends reaches a prompt
unbounded. The event stream ends
inside `ServiceOptions.OperationTimeout`, and the client resumes it from
`Last-Event-ID`, the shape
[ADR-108](ADR-108-the-buyers-order-has-a-server-sent-event-stream-that-polls-its-row-and-ends-inside-the-deadline-475.md)
gave the order stream. In §15.1's filter the `bff` filter takes an `inputs`
anchor that keeps the two blocks, and `shared`, which every service takes, is
`inputs` less the two blocks.
**Why.** The BFF is the platform's customer-facing host and already
authenticates the customer, so it is where the second host of
[ADR-149](ADR-149-the-ai-assistants-core-and-its-adapter-are-two-building-blocks-this-repository-publishes-as-nuget-packages-1346.md)'s
core belongs. A turn runs off the request, after the response that started it,
so the customer's token is neither in reach nor alive for long enough (§11.3),
and it could read neither Inventory nor Payments in any case; the projection
already answers a customer's order, payment and shipment by subject, with no
hop. Redaction by pattern is a floor rather than a classifier: it keeps the
contact details a customer most often types out of a third party's hands, and
it leaves the order facts the assistant exists to explain. The gateway's
per-subject bucket (§10.3) protects the platform's capacity and cannot see a
provider's cost, so the limit on what a customer can spend is the host's.
Filtering the BFF by `inputs` rebuilds the one image that holds the blocks,
where dropping
[ADR-150](ADR-150-the-assistant-packages-version-rule-is-checked-on-the-pull-request-and-their-blocks-leave-the-shared-filter-1447-amending-adr-149.md)'s
exclusions would rebuild every image again.
**Consequences.** A conversation lives in the memory of the replica that
opened it, and the BFF runs several behind a Service with no affinity, so a
deployed BFF keeps the assistant off: its chart carries no `Assistant` key,
no egress to the provider and no secret, and #1451 owns turning it on. Local
Compose runs one BFF, where it works. Stock cannot be answered, since no
stock read is open to a customer, and the host's instructions tell the model
so; #1452 owns it. A conversation's text is personal data held past
`AssistantOptions.IdleTimeout` until the replica next opens or finds a
conversation (#1457), with no erasure path, which
`docs/personal-data.md` records as lifetime only, and `MaxCallers` bounds the
customers one replica holds at once. The redactor also replaces a tracking
number that happens to pass the Luhn check. The provider's call is the SDK's
own client, with its timeout and retries and no breaker: §9.7's rules are for a
hop a request waits on, and no request waits on a turn, whose failure reaches
the client as the stream's error event; the status route waits only on the
adapter's cached model probe, inside the request's deadline, and that probe,
shared across requests, carries no one request's correlation ID (§10.4).
ADR-053's rule 3 names a processor by a chart value, and this one is named by
`Assistant:Provider` and reached at the SDK's own address, an exception that
holds only while no deployment turns the assistant on; #1451 gives the chart
its value. §12.7's third-party row is owed with #1451 as well, since the BFF's
suite drives the host with a scripted provider. The chart's capability guards,
§15.4's key table and `docs/secrets.md` gain their entries with #1451, not
here. The host's 429s, from its limiter and from a full conversation store,
carry no `Retry-After` (§10.5): §10.3's one form of the header is the
gateway's `RetryAfterHeader`, which this host does not reference. ADR-053,
ADR-149 and ADR-150 are otherwise unchanged; this decision is read beside
them, as an ADR is amended and never rewritten.

**Amended by
[ADR-152](ADR-152-the-assistant-core-sweeps-idle-conversations-on-a-timer-so-idletimeout-plus-a-quarter-bounds-retention-1457-amending-adr-151.md)**:
the core now sweeps idle conversations on a timer, so the retention bound
above is `IdleTimeout` plus a quarter of it (a second where that is longer),
not the next open or find. This record is otherwise unchanged.

**Amended by
[ADR-153](ADR-153-the-deployed-bff-runs-the-assistant-on-a-dedicated-single-replica-release-web-bff-assistant-behind-its-own-gateway-route-1451-amending-adr-151.md)**:
the deployed BFF runs the assistant on its own single-replica release behind its
own gateway route, so the main release keeps it off by design and #1451 is
the set of pieces that make that true. This record is otherwise unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
