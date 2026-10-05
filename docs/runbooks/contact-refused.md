# Runbook — contact reads refused

| | |
|---|---|
| Alert | `ContactReadRefused`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | Any `notifications.contact.refused` in the alert's window, which outlasts a row's longest backoff |
| Signal | `ContactMetrics`, `src/Services/Notifications/Notifications.Infrastructure/Contacts` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The team that owns Notifications ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Notifications' send worker reads a customer's mailbox and language from
Keycloak's admin API under its own client credential
([ADR-052](../backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)),
and that read was refused: by the identity provider, by the worker's own check
of the grant on the token it was issued, or by Keycloak's admin API. **Every
notification whose customer has no fresh stored contact backs off and is not
sent**, and no retry changes the answer, because the cause is a credential or
a grant. A refused read is never answered from a stale stored contact, as an
outage is: the refusal is the deployment's to fix, not a reason to send to an
address the owner may have changed.

## What is *not* affected

- **Orders, payments and shipments.** Notifications publishes nothing
  ([§3.2](../backend-architecture/03-bounded-contexts.md)); no other service
  waits on it.
- **A notification whose customer's contact is fresh.** It is sent from the
  stored row without asking Keycloak (`ContactOptions.Freshness`).
- **A row younger than its give-up age.** It backs off and is claimed again
  on a later pass. **A row that reaches `DeliveryOptions.GiveUpAge` while
  reads are refused is not**: the worker marks it `Undeliverable` with the
  reason `gave_up`, and that customer is never told.

## Telling it from an outage

Both hold rows on the `contact` step of `notifications.waiting`. An outage —
Keycloak unreachable, slow or answering 5xx — counts nothing here, serves a
stale contact where one is stored, and clears when Keycloak does. A refusal
counts here and clears only when somebody fixes it.

```promql
sum(rate(notifications_contact_refused_total[10m]))

max by (step) (notifications_waiting)
```

## Find the cause

The worker logs each refused row as `ContactRefused` — *"The contact read for
notification … was refused over this host's credential; the row backs off"* —
with the refusal as its exception. Its message names which of four it is:

| The exception says | The cause |
|---|---|
| *did not issue this host a usable token* | The identity provider refused the `notifications-worker` client: its secret is wrong or rotated on one side only, or the client is disabled |
| *realm-management role(s) where ADR-052 names* | The `service-account-notifications-worker` user no longer holds exactly its grant on `realm-management`, one too few or one too many |
| *a token that is not a JWT* | The realm is issuing opaque tokens to this client |
| *Keycloak refused this host's token with …* | The admin API rejected a token the worker accepted: `401` is a token it does not accept for this realm, `403` a grant it does not find |

The worker's own check refuses before the admin API is asked, so the first
three never reach it.

## Fix it

- **A secret.** The worker reads it from the Secret its chart names under
  `identity.clientSecretRef` (`deploy/helm/notifications/values.yaml`). Make
  it match the client's secret in the realm by
  [`docs/secrets.md`](../secrets.md)'s client-secret procedure, then restart
  the worker so it reads the new value.
- **A grant.** Give the service account back exactly the grant
  [`docs/secrets.md`](../secrets.md) names for it, and nothing more. A wider
  grant is refused too, deliberately: the worker holding more than it reads is
  the failure ADR-052 exists to prevent.
- **An issuer or realm.** Compare `identity.authority` with
  `contactSource.baseUrl` and `contactSource.realm`; the render refuses a
  realm that is not the authority's, so a mismatch here is a base URL pointing
  at another Keycloak.

The backed-off rows are claimed again on their own, so once the rate falls to
zero, watch the `contact` step drain and the alert resolve. **A waiting set
that falls is not by itself recovery**: rows given up during the refusal leave
it too. Look for rows in `notifications.NotificationLog` marked
`Undeliverable` with `gave_up` since the refusal began; each is a customer who
was not told, and whether to tell them by hand is the business's decision.
