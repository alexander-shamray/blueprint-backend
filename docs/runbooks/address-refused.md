# Runbook — delivery-address reads refused

| | |
|---|---|
| Alert | `AddressReadRefused`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | Any `shipping.address.refused` in the last 5 minutes |
| Signal | `AddressMetrics`, `src/Services/Shipping/Shipping.Infrastructure/Addresses` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The team that owns Shipping ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Shipping's fulfilment worker reads each order's delivery address from
Ordering under its own client credential
([ADR-052](../backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)),
and that read was refused: by the identity provider, by the worker's own check
of the grant on the token it was issued, or by Ordering. **Every shipment that
has no stored address yet backs off and retries without leaving**, and no
retry changes the answer, because the cause is a credential or a grant.

## What is *not* affected

- **Placing and paying.** Ordering takes the order and Payments authorises
  it without Shipping; only the saga's wait for a despatch lengthens.
- **A shipment whose address is already stored.** It is booked as usual; only
  the read is refused.
- **A row younger than its give-up age.** It backs off and is claimed again
  on a later pass. **A row that reaches `FulfilmentOptions.GiveUpAge` while
  reads are refused is not**: the worker ends it as unfulfillable with the
  reason `gave_up`, and by then the saga has raised the order for review.

## Find the cause

The worker logs each backed-off row as `PassFailed` — *"Fulfilment pass for
shipment … failed; the row backs off"* — with the refusal as its exception.
Its message names which of four it is:

| The exception says | The cause |
|---|---|
| *did not issue this host a usable token* | The identity provider refused the `shipping-worker` client: its secret is wrong or rotated on one side only, or the client is disabled |
| *permission(s) where ADR-052 names exactly one* | The `service-account-shipping-worker` user no longer holds exactly `orders:delivery-address` on `commerce-api`, one too few or one too many |
| *a token that is not a JWT* | The realm is issuing opaque tokens to this client |
| *Ordering refused this host's token with …* | Ordering rejected a token the worker accepted: `Unauthenticated` is an issuer or audience the two hosts disagree on, `PermissionDenied` a grant Ordering's policy does not find |

The worker's own check refuses before Ordering is asked, so the first three
never reach Ordering's logs.

## Fix it

- **A secret.** The worker reads it from the Secret its chart names under
  `identity.clientSecretRef` (`deploy/helm/shipping/values.yaml`). Make it
  match the client's secret in the realm, then restart the worker so it reads
  the new value.
- **A grant.** Give the service account back exactly
  `orders:delivery-address` and nothing more. A second permission is refused
  too, deliberately: the worker holding more than it reads is the failure
  ADR-052 exists to prevent.
- **An issuer or audience.** Compare the two hosts' `Identity` configuration;
  both name the one realm ([§11.3](../backend-architecture/11-identity-authorization.md)).

The backed-off rows are claimed again on their own, so once the rate falls to
zero, watch `shipping.shipments.waiting` drain and the alert resolve. **A
waiting set that falls is not by itself recovery**: rows given up during the
refusal leave it too. Look for shipments marked unfulfillable with `gave_up`
since the refusal began, and work their orders from `order-review.md`.
