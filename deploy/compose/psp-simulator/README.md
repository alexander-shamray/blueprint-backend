# Payment provider simulator

[§3.2](../../../docs/backend-architecture/03-bounded-contexts.md)'s payment
provider, simulated: a WireMock.Net server loaded with the mappings in
`mappings/`, which the adapter's own tests and this Compose unit both read.
Section 9 of [the service design
spec](../../../docs/superpowers/specs/2026-09-18-payments-service-design.md)
is the specification; this file is what a person at the keyboard needs.

## Scripted amounts

The verdict is scripted by the amount's minor units:

| Minor units | Answer |
|---|---|
| `.01` | 402 `card_declined` |
| `.02` | 402 `insufficient_funds` |
| `.05` | 503 |
| `.09` | a delay past the adapter's total |
| any other | 201 `approved` |
| a void | 200 |

The reference is `psp_` followed by the request's `Idempotency-Key` header,
templated from the request, so a replay of the same key answers the same
reference.
