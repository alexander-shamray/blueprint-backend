# Appendix A — Architecture decision records

Short-form ADRs, one file each under `adr/`, indexed here. Each records what
was decided, why, and what it costs. The value is in each one's
**Consequences.** sentence — that is what a future reader needs when the
decision looks wrong.

| | |
|---|---|
| **ADR-001** | [Database per service](adr/ADR-001-database-per-service.md) |
| **ADR-002** | [Async messaging as the default](adr/ADR-002-async-messaging-as-the-default.md) |
| **ADR-003** | [MassTransit v8, pinned](adr/ADR-003-masstransit-v8-pinned.md) |
| **ADR-004** | [No mediator library](adr/ADR-004-no-mediator-library.md) |
| **ADR-005** | [EF Core for writes, Dapper for reads](adr/ADR-005-ef-core-for-writes-dapper-for-reads.md) |
| **ADR-006** | [Redis for cache and coordination, never as a store of record](adr/ADR-006-redis-for-cache-and-coordination-never-as-a-store-of-record.md) |
| **ADR-007** | [Migrations as a pre-deploy job](adr/ADR-007-migrations-as-a-pre-deploy-job.md) |
| **ADR-008** | [YARP as the gateway](adr/ADR-008-yarp-as-the-gateway.md) |
| **ADR-009** | [Keycloak, not a hand-built identity service](adr/ADR-009-keycloak-not-a-hand-built-identity-service.md) |
| **ADR-010** | [Testcontainers, not in-memory providers](adr/ADR-010-testcontainers-not-in-memory-providers.md) |
| **ADR-011** | [Compose baseline, Aspire optional](adr/ADR-011-compose-baseline-aspire-optional.md) |
| **ADR-012** | [Contracts versioned by namespace](adr/ADR-012-contracts-versioned-by-namespace.md) |
| **ADR-013** | [Dapr not adopted](adr/ADR-013-dapr-not-adopted.md) |
| **ADR-014** | [Wolverine not adopted, but kept as the exit](adr/ADR-014-wolverine-not-adopted-but-kept-as-the-exit.md) |
| **ADR-015** | [Minimal APIs, not MVC controllers](adr/ADR-015-minimal-apis-not-mvc-controllers.md) |
| **ADR-016** | [Cursor pagination by default](adr/ADR-016-cursor-pagination-by-default.md) |
| **ADR-017** | [One synchronous hop](adr/ADR-017-one-synchronous-hop.md) |
| **ADR-018** | [Reactions happen after commit](adr/ADR-018-reactions-happen-after-commit.md) |
| **ADR-019** | [Warnings are errors, and the .editorconfig is a build input](adr/ADR-019-warnings-are-errors-and-the-editorconfig-is-a-build-input.md) |
| **ADR-020** | [The edge compresses over TLS, and says so](adr/ADR-020-the-edge-compresses-over-tls-and-says-so.md) |
| **ADR-021** | [Saga timeouts are scheduled by the broker](adr/ADR-021-saga-timeouts-are-scheduled-by-the-broker.md) |
| **ADR-022** | [The canary is a second release, weighted by replicas](adr/ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md) |
| **ADR-023** | [The consumer-driven contract is a linked file, not Pact](adr/ADR-023-the-consumer-driven-contract-is-a-linked-file-not-pact.md) |
| **ADR-024** | [A release answers for the order, not for the reservation](adr/ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md) |
| **ADR-025** | [A saga state that waits on two services finalises on neither alone](adr/ADR-025-a-saga-state-that-waits-on-two-services-finalises-on-neither-alone.md) |
| **ADR-026** | [Consumer capability is a release ahead of the producer that uses it](adr/ADR-026-consumer-capability-is-a-release-ahead-of-the-producer-that-uses-it.md) |
| **ADR-027** | [The order summary stores product ids and resolves the name locally](adr/ADR-027-the-order-summary-stores-product-ids-and-resolves-the-name-locally.md) |
| **ADR-028** | [A money-movement command carries no subject](adr/ADR-028-a-money-movement-command-carries-no-subject.md) |
| **ADR-029** | [Inventory releases on the cancellation, not on the saga's word](adr/ADR-029-inventory-releases-on-the-cancellation-not-on-the-sagas-word.md) |
| **ADR-030** | [Authorization is deny by default, in the building block](adr/ADR-030-authorization-is-deny-by-default-in-the-building-block.md) |
| **ADR-031** | [The service owns `nosniff`; the Ingress owns HSTS](adr/ADR-031-the-service-owns-nosniff-the-ingress-owns-hsts.md) |
| **ADR-032** | [The saga's outbox is MassTransit's, in the saga's own transaction](adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md) |
| **ADR-033** | [Revocation is bounded by the token lifetime, and no denylist exists](adr/ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md) |
| **ADR-034** | [The browser holds an access token and no refresh token](adr/ADR-034-the-browser-holds-an-access-token-and-no-refresh-token.md) |
| **ADR-035** | [An integration event carries identifiers, not personal data](adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md) |
| **ADR-036** | [The broker has a per-service identity](adr/ADR-036-the-broker-has-a-per-service-identity.md) |
| **ADR-037** | [The idempotency marker is a row in the command's own transaction](adr/ADR-037-the-idempotency-marker-is-a-row-in-the-commands-own-transaction.md) |
| **ADR-038** | [The marker and its claim are ordered by construction, not a margin](adr/ADR-038-the-marker-and-its-claim-are-ordered-by-construction-not-a-margin.md) |
| **ADR-039** | [The marker's purge asks the claim rather than out-counting it](adr/ADR-039-the-markers-purge-asks-the-claim-rather-than-out-counting-it.md) |
| **ADR-040** | [No host accepts a token with more life left than the revocation bound](adr/ADR-040-no-host-accepts-a-token-with-more-life-left-than-the-revocation-bound.md) |
| **ADR-041** | [The marker's delete identifies a row by a rowversion, not a timestamp](adr/ADR-041-the-markers-delete-identifies-a-row-by-a-rowversion-not-a-timestamp.md) |
| **ADR-042** | [The deployed realm is checked at deploy time](adr/ADR-042-the-deployed-realm-is-checked-at-deploy-time.md) |
| **ADR-043** | [The deployed realm is checked between rollouts](adr/ADR-043-the-deployed-realm-is-checked-between-rollouts.md) |
| **ADR-044** | [The native client holds a refresh token, and the realm rotates it](adr/ADR-044-the-native-client-holds-a-refresh-token-and-the-realm-rotates-it.md) |
| **ADR-045** | [The checkout quote takes quantities](adr/ADR-045-the-checkout-quote-takes-quantities.md) |
| **ADR-046** | [Each client declares a browser origin, and the gate asserts the shape](adr/ADR-046-each-client-declares-a-browser-origin-and-the-gate-asserts-the-shape.md) |
| **ADR-047** | [The canary judges each workload on the signals it receives](adr/ADR-047-the-canary-judges-each-workload-on-the-signals-it-receives.md) |

---

[← §15 CI/CD](15-cicd-deployment.md) · [Index](README.md) · [Appendix B →](appendix-b-licences.md)
