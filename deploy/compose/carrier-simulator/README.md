# Carrier simulator

[§3.2](../../../docs/backend-architecture/03-bounded-contexts.md)'s carrier,
simulated: a WireMock.Net server loaded with the mappings in `mappings/`,
which the adapter's own tests and this Compose unit both read. Section 9 of
[the service design
spec](../../../docs/superpowers/specs/2026-09-22-shipping-service-design.md)
is the specification; this file is what a person at the keyboard needs.

## Scripted postal codes

The answer is scripted by the delivery address's postal code, because it is
the one field the carrier sees that a person placing a Compose order
controls:

| Postal code | Answer |
|---|---|
| `SIM-REFUSED` | the booking answers 422 `address_not_serviceable` |
| `SIM-DOWN` | the booking answers 503 |
| `SIM-SLOW` | the booking answers after a delay past `CarrierHop`'s total |
| `SIM-TRANSIT` | events: `collected` and nothing after it |
| `SIM-REVERSED` | events: `delivered` on the page before `collected` |
| `SIM-STRANGE` | events: a status nobody agreed, a timestamp from next year, a link on a foreign host |
| `SIM-LATE` | a cancel answers 409 `already_collected` |
| any other | booked; events `collected` then `delivered` |

The simulator holds no state, so the booking's reference is literal per
script — `crr_SIM-TRANSIT`, `crr_SIM-LATE`, and `crr_SIM-OK` for every
unscripted code — and the events and cancel mappings match on that
reference's path. The feed's timestamps are fixed for the same reason.

## Watching each one

Place an order whose delivery postal code is one of the codes above, then
read the fulfilment and tracking workers' logs:

- `SIM-DOWN`: the fulfilment worker backs off, and books once the code
  changes.
- `SIM-SLOW`: each attempt times out, the call is abandoned inside the total
  budget, and the row backs off as it does for `SIM-DOWN`.
- `SIM-REFUSED`: the shipment turns `Unfulfillable` with the carrier's reason.
- `SIM-TRANSIT` and `SIM-REVERSED`: tracking stops at collected, or promotes
  by rank whatever order the page arrives in.
- `SIM-STRANGE`: the page is refused whole and the poll backs off.
- `SIM-LATE`: a cancel after booking is answered too late, and the saga's
  review row is the record.

The server's own view is at `http://localhost:5191/__admin/mappings` and
`/__admin/requests`.
