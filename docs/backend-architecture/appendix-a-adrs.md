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
| **ADR-048** | [A reservation row is kept for as long as its order](adr/ADR-048-a-reservation-row-is-kept-for-as-long-as-its-order.md) |
| **ADR-049** | [A cancellation Payments has recorded declines the authorisation that follows](adr/ADR-049-a-cancellation-payments-has-recorded-declines-the-authorisation-that-follows.md) |
| **ADR-050** | [The rollout reads the image's facts from the image's revision](adr/ADR-050-the-rollout-reads-the-images-facts-from-its-revision.md) |
| **ADR-051** | [The buyer's order read is a projection in the BFF](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md) |
| **ADR-052** | [A contact is read from its owner by a worker, and kept in the reader's own table](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md) |
| **ADR-053** | [A jurisdiction is a value the deployment is given](adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md) |
| **ADR-054** | [A shipment stops waiting on its carrier at an age](adr/ADR-054-a-shipment-stops-waiting-on-its-carrier-at-an-age.md) |
| **ADR-055** | [An outbound hop registers beside its layer, and the host calls it](adr/ADR-055-an-outbound-hop-registers-beside-its-layer-and-the-host-calls-it.md) |
| **ADR-056** | [A service's fixture derives from one shared body under tests/](adr/ADR-056-a-services-fixture-derives-from-one-shared-body-under-tests.md) |
| **ADR-057** | [A command id is bound to the fingerprint of the command that claimed it](adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md) |
| **ADR-058** | [A write endpoint is keyed or declares why a repeat is harmless](adr/ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md) |
| **ADR-059** | [An entry with no fingerprint is refused as already committed](adr/ADR-059-an-entry-with-no-fingerprint-is-refused-as-already-committed.md) |
| **ADR-060** | [The already-committed refusal says its result cannot be returned](adr/ADR-060-the-already-committed-refusal-says-its-result-cannot-be-returned.md) |
| **ADR-061** | [A keyed write endpoint is keyed by one command](adr/ADR-061-a-keyed-write-endpoint-is-keyed-by-one-command.md) |
| **ADR-062** | [The domain is a reference implementation](adr/ADR-062-the-domain-is-a-reference-implementation.md) |
| **ADR-063** | [A worker that waits on a third party is scaled by hand](adr/ADR-063-a-worker-that-waits-on-a-third-party-is-scaled-by-hand.md) |
| **ADR-064** | [Every store is run outside this repository, to a stated recovery point](adr/ADR-064-every-store-is-run-outside-this-repository-to-a-stated-recovery-point.md) |
| **ADR-065** | [Every workload is fenced by a default-deny NetworkPolicy](adr/ADR-065-every-workload-is-fenced-by-a-default-deny-networkpolicy.md) |
| **ADR-066** | [A request past its host's deadline is answered 504](adr/ADR-066-a-request-past-its-hosts-deadline-is-answered-504.md) |
| **ADR-067** | [A currency's minor unit is ISO 4217's, held once](adr/ADR-067-a-currencys-minor-unit-is-iso-4217s-held-once.md) |
| **ADR-068** | [The jurisdiction record's log lifetime and incident procedure are delivered](adr/ADR-068-the-jurisdiction-records-log-lifetime-and-incident-procedure-are-delivered.md) |
| **ADR-069** | [The migration Job is fenced by a hook policy of its own](adr/ADR-069-the-migration-job-is-fenced-by-a-hook-policy-of-its-own.md) |
| **ADR-070** | [Payments calls its provider inside its consumers](adr/ADR-070-payments-calls-its-provider-inside-its-consumers.md) |
| **ADR-071** | [Every image carries an SBOM, and signing waits on a registry](adr/ADR-071-every-image-carries-an-sbom-and-signing-waits-on-a-registry.md) |
| **ADR-072** | [A person replays dead letters as a broker account of its own](adr/ADR-072-a-person-replays-dead-letters-as-a-broker-account-of-its-own.md) |
| **ADR-073** | [The product listing takes a search and a closed sort, and its cursor carries both](adr/ADR-073-the-product-listing-takes-a-search-and-a-closed-sort-and-its-cursor-carries-both.md) |
| **ADR-074** | [A seller reads their own products and withdraws one, and a withdrawal is final](adr/ADR-074-a-seller-reads-their-own-products-and-withdraws-one-and-a-withdrawal-is-final.md) |
| **ADR-075** | [A product's events are stamped in the order its writes committed](adr/ADR-075-a-products-events-are-stamped-in-the-order-its-writes-committed.md) |
| **ADR-076** | [The realm check refuses a credential wider than a read](adr/ADR-076-the-realm-check-refuses-a-credential-wider-than-a-read.md) |
| **ADR-077** | [A worker's token is capped by its client's scope, and the realm gate reads the cap](adr/ADR-077-a-workers-token-is-capped-by-its-clients-scope-and-the-realm-gate-reads-the-cap.md) |
| **ADR-078** | [The realm check requires view-realm](adr/ADR-078-the-realm-check-requires-view-realm.md) |
| **ADR-079** | [Outside Development, an infrastructure connection is encrypted unless the deployer names it plaintext](adr/ADR-079-outside-development-an-infrastructure-connection-is-encrypted-unless-the-deployer-names-it-plaintext.md) |
| **ADR-080** | [A NetworkPolicy peer is refused wider than a /8, or with selectors that name nothing](adr/ADR-080-a-networkpolicy-peer-is-refused-wider-than-a-8-or-with-selectors-that-name-nothing.md) |
| **ADR-081** | [A workload image may be pinned by digest, and the rollout pins it once a registry exists](adr/ADR-081-a-workload-image-may-be-pinned-by-digest-and-the-rollout-pins-it-once-a-registry-exists.md) |
| **ADR-082** | [Every pod meets Pod Security "restricted", with a read-only root filesystem](adr/ADR-082-every-pod-meets-pod-security-restricted-with-a-read-only-root-filesystem.md) |
| **ADR-083** | [The edge starts a root trace, and a caller's trace context stops there](adr/ADR-083-the-edge-starts-a-root-trace-and-a-callers-trace-context-stops-there.md) |
| **ADR-084** | [A third party's answer is held to a character set before it is recorded](adr/ADR-084-a-third-partys-answer-is-held-to-a-character-set-before-it-is-recorded.md) |
| **ADR-085** | [A published product's name is visible text, and its thumbnail is https from a host the seller chooses](adr/ADR-085-a-published-products-name-is-visible-text-and-its-thumbnail-is-https-from-a-host-the-seller-chooses.md) |
| **ADR-086** | [An unverified email is no contact](adr/ADR-086-an-unverified-email-is-no-contact.md) |
| **ADR-087** | [A caller cancels an order only as a customer request](adr/ADR-087-a-caller-cancels-an-order-only-as-a-customer-request.md) |
| **ADR-088** | [The realm gate judges a worker's cap expanded, and the audience scope's mappers exactly](adr/ADR-088-the-realm-gate-judges-a-workers-cap-expanded-and-the-audience-scopes-mappers-exactly.md) |
| **ADR-089** | [The browser-origin obligation binds the clients that exchange a code from a page](adr/ADR-089-the-browser-origin-obligation-binds-the-clients-that-exchange-a-code-from-a-page.md) |
| **ADR-090** | [Catalog republishes the facts it still holds, from the migrator image, with their original stamps](adr/ADR-090-catalog-republishes-the-facts-it-still-holds-from-the-migrator-image-with-their-original-stamps.md) |
| **ADR-091** | [A host holding client credentials makes a call under a grant of its own, and not always to a peer](adr/ADR-091-a-host-holding-client-credentials-makes-a-call-under-a-grant-of-its-own-and-not-always-to-a-peer.md) |
| **ADR-092** | [Privacy is a seventh service, and its responder set is fixed when a request is raised](adr/ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md) |
| **ADR-093** | [The journey test walks one order across the services and asserts that they converge](adr/ADR-093-the-journey-test-walks-one-order-across-the-services-and-asserts-that-they-converge.md) |
| **ADR-094** | [A holder reports its erasure by sending to Privacy's queue, and does not publish it](adr/ADR-094-a-holder-reports-its-erasure-by-sending-to-privacys-queue-and-does-not-publish-it.md) |
| **ADR-095** | [A closed erasure request is kept as the evidence, and has no retention window](adr/ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md) |
| **ADR-096** | [A chapter names a reference service only as evidence](adr/ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md) |
| **ADR-097** | [A holder that writes from a feed asks its own erasure rows after it writes](adr/ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md) |
| **ADR-098** | [The BFF remembers the id of an order it erased](adr/ADR-098-the-bff-remembers-the-id-of-an-order-it-erased.md) |
| **ADR-099** | [An order carries the erasure requests its customer was already under when it was placed](adr/ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md) |
| **ADR-100** | [A send pass asks its erasure rows after it saves a contact](adr/ADR-100-a-send-pass-asks-its-erasure-rows-after-it-saves-a-contact.md) |
| **ADR-101** | [An order record keeps the requests its order was placed under, and the send pass asks them before the owner](adr/ADR-101-an-order-record-keeps-the-requests-its-order-was-placed-under-and-the-send-pass-asks-them-before-the-owner.md) |
| **ADR-102** | [Remove-service lists what still names a removed service and edits no document](adr/ADR-102-remove-service-lists-what-still-names-a-removed-service-and-edits-no-document.md) |
| **ADR-103** | [Shipping keeps the erasure requests an order was placed under on its shipment](adr/ADR-103-shipping-keeps-the-erasure-requests-an-order-was-placed-under-on-its-shipment.md) |
| **ADR-104** | [An operator may switch off Notifications, Shipping and Privacy for a bound, and no other service](adr/ADR-104-an-operator-may-switch-off-notifications-shipping-and-privacy-for-a-bound-and-no-other-service.md) |
| **ADR-105** | [Appendix E has no per-service unit, so remove-service still edits no document](adr/ADR-105-appendix-e-has-no-per-service-unit-so-remove-service-still-edits-no-document.md) |
| **ADR-106** | [An S3-compatible object store for product images; the BFF issues a pre-signed upload URL (#472)](adr/ADR-106-an-s3-compatible-object-store-for-product-images-the-bff-issues-a-pre-signed-upload-url-472.md) |
| **ADR-107** | [The BFF keeps a customer's saved delivery address, and an erasure deletes it (#473)](adr/ADR-107-the-bff-keeps-a-customers-saved-delivery-address-and-an-erasure-deletes-it-473.md) |
| **ADR-108** | [The buyer's order has a server-sent event stream that polls its row and ends inside the deadline (#475)](adr/ADR-108-the-buyers-order-has-a-server-sent-event-stream-that-polls-its-row-and-ends-inside-the-deadline-475.md) |
| **ADR-109** | [Images are pushed to GHCR, signed and attested by digest on main, and the rollout verifies them before Helm (#438)](adr/ADR-109-images-are-pushed-to-ghcr-signed-and-attested-by-digest-on-main-and-the-rollout-verifies-them-before-helm-438.md) |
| **ADR-110** | [The deployed object store is checked between rollouts: each image credential pair is asked to delete and to list, and must be refused (#985)](adr/ADR-110-the-deployed-object-store-is-checked-between-rollouts-each-image-credential-pair-is-asked-to-delete-and-to-list-and-must-be-refused-985.md) |
| **ADR-111** | [A red object-store.yml run files a tracker issue, or comments on the open one (#988); narrows ADR-110's consequence that a red run files no issue](adr/ADR-111-a-red-object-storeyml-run-files-a-tracker-issue-or-comments-on-the-open-one-988-narrows-adr-110s-consequence-that-a-red-run-files-no-issue.md) |
| **ADR-112** | [The saga's outbox transaction is ReadCommitted and only the saga's row lock is serializable (#992); supersedes ADR-032's Serializable outbox transaction](adr/ADR-112-the-sagas-outbox-transaction-is-readcommitted-and-only-the-sagas-row-lock-is-serializable-992-supersedes-adr-032s-serializable-outbox-transaction.md) |
| **ADR-113** | [An operator switches a receive endpoint or a worker off from a file that reloads, and no feature-flag library is adopted (#954)](adr/ADR-113-an-operator-switches-a-receive-endpoint-or-a-worker-off-from-a-file-that-reloads-and-no-feature-flag-library-is-adopted-954.md) |
| **ADR-114** | [The object store check sends a heartbeat on a green run and is provisioned by a script, and can name a virtual-hosted bucket (#995, #996)](adr/ADR-114-the-object-store-check-sends-a-heartbeat-and-is-provisioned-by-a-script-995-996.md) |
| **ADR-115** | [An operator-switched name is reported by an operator-switches readiness check that is Degraded, supersedes ADR-113's loop that adds no check (#1013)](adr/ADR-115-an-operator-switched-name-is-reported-by-an-operator-switches-readiness-check-that-is-degraded-supersedes-adr-113s-loop-that-adds-no-check-1013.md) |
| **ADR-116** | [A gateway route to a service an operator may stop is switched off in its configuration and answers 503 at the edge (#955)](adr/ADR-116-a-gateway-route-to-a-service-an-operator-may-stop-is-switched-off-in-its-configuration-and-answers-503-at-the-edge-955.md) |
| **ADR-117** | [A device's push token is a contact Notifications owns, registered through the BFF and erased with its subject (#474)](adr/ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md) |
| **ADR-118** | [Notifications gains an API host for device registration and preferences, and the push adapters stay in the worker (#474)](adr/ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md) |
| **ADR-119** | [An operator switches a gateway route from a file that reloads, and the gateway's chart renders it (#1022)](adr/ADR-119-an-operator-switches-a-gateway-route-from-a-file-that-reloads-and-the-gateway-chart-renders-it-1022.md) |
| **ADR-120** | [Channel preferences are a consent journal Notifications keeps per subject, topic and channel, and an erasure anonymises it (#474)](adr/ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md) |
| **ADR-121** | [A sign-out above a cleared device row moves its sequence whoever sends it, and an invalid-token answer keeps the row (#1034, #474)](adr/ADR-121-a-sign-out-above-a-cleared-device-row-moves-its-sequence-whoever-sends-it-and-an-invalid-token-answer-keeps-the-row-1034-474.md) |
| **ADR-122** | [Notifications' two hosts roll in lockstep, so a migration's previous release is the one both hosts run (#1038)](adr/ADR-122-notifications-two-hosts-roll-in-lockstep-so-a-migrations-previous-release-is-the-one-both-hosts-run-1038.md) |
| **ADR-123** | [A send is decided once, by the pass that commits its intent, and a resend under that intent is not decided again (#1041, #474)](adr/ADR-123-a-send-is-decided-once-by-the-pass-that-commits-its-intent-and-a-resend-under-that-intent-is-not-decided-again-1041-474.md) |
| **ADR-124** | [The Notifications worker reaches each push provider an app names, on the port its address dials (#474)](adr/ADR-124-the-notifications-worker-reaches-each-push-provider-an-app-names-on-the-port-its-address-dials-474.md) |
| **ADR-125** | [A value two releases are each given is held equal before either rolls (#1042)](adr/ADR-125-a-value-two-releases-are-each-given-is-held-equal-before-either-rolls-1042.md) |
| **ADR-126** | [The rollout keeps a values path empty while either track serves a revision older than the one its descriptor names (#1059)](adr/ADR-126-the-rollout-keeps-a-values-path-empty-while-either-track-serves-a-revision-older-than-the-one-its-descriptor-names-1059.md) |
| **ADR-127** | [A release does not roll back across a revision its descriptor holds a value empty until (#1060)](adr/ADR-127-a-release-does-not-roll-back-across-a-revision-its-descriptor-holds-a-value-empty-until-1060.md) |
| **ADR-128** | [The saga's five timeout delays are options bound from configuration, defaulting to §9.6's constants, with a Compose override that shortens them (#1072)](adr/ADR-128-the-sagas-five-timeout-delays-are-options-bound-from-configuration-defaulting-to-96s-constants-with-a-compose-override-that-shortens-them-1072.md) |
| **ADR-129** | [A carrier booking carries its order's id beside the shipment's (#1074)](adr/ADR-129-a-carrier-booking-carries-its-orders-id-beside-the-shipments-1074.md) |
| **ADR-130** | [The options validator is held once in Common.Infrastructure (#1075), superseding ADR-128's sentence that the Ordering validator is a copy of Shipping's](adr/ADR-130-the-options-validator-is-held-once-in-commoninfrastructure-1075-superseding-adr-128s-sentence-that-the-ordering-validator-is-a-copy-of-shippings.md) |
| **ADR-131** | [The switched polling loop is held once in Common.Infrastructure.Switching.SwitchedPollingWorker (#1083), adding to ADR-113](adr/ADR-131-the-switched-polling-loop-is-held-once-in-commoninfrastructureswitchingswitchedpollingworker-1083-adding-to-adr-113.md) |
| **ADR-132** | [The workflow-paths-cover-inputs check is held once in deploy/workflow_paths (#1082), used by four Python gates and smoke.sh](adr/ADR-132-the-workflow-paths-cover-inputs-check-is-held-once-in-deploy-workflow_paths-1082-used-by-four-python-gates-and-smokesh.md) |
| **ADR-133** | [The domain-name gate matches service names only, and a feature written in other nouns is caught by review, not the gate (#1095), adding to ADR-096](adr/ADR-133-the-domain-name-gate-matches-service-names-only-and-a-feature-written-in-other-nouns-is-caught-by-review-not-the-gate-1095-adding-to-adr-096.md) |
| **ADR-134** | [Each image has a `main` tag that names its newest signed build, and Compose names the image beside its build (#1111), adding to ADR-109](adr/ADR-134-each-image-has-a-main-tag-that-names-its-newest-signed-build-and-compose-names-the-image-beside-its-build-1111-adding-to-adr-109.md) |
| **ADR-135** | [A saved address carries the erasure requests its customer was already under when it was saved (#1172), amending ADR-107](adr/ADR-135-a-saved-address-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-saved-1172-amending-adr-107.md) |
| **ADR-136** | [An operator may amend an unclosed erasure request's responder set, recorded with who, when and why (#1177), amending ADR-092](adr/ADR-136-an-operator-may-amend-an-unclosed-erasure-requests-responder-set-recorded-with-who-when-and-why-1177-amending-adr-092.md) |
| **ADR-137** | [A placement and an erasure for one customer take one application lock and run one after the other (#1191), amending ADR-099](adr/ADR-137-a-placement-and-an-erasure-for-one-customer-take-one-application-lock-and-run-one-after-the-other-1191-amending-adr-099.md) |
| **ADR-138** | [Notifications' erasure keeps what was written under its request (#1192), amending ADR-117 and ADR-120](adr/ADR-138-notifications-erasure-keeps-what-was-written-under-its-request-1192-amending-adr-117-and-adr-120.md) |
| **ADR-139** | [An erasure check asks no request that closed before its data left the owner (#882), amending ADR-092, ADR-097, ADR-099, ADR-135, ADR-136 and ADR-138](adr/ADR-139-an-erasure-check-asks-no-request-that-closed-before-its-data-left-the-owner-882-amending-adr-092-adr-097-adr-099-adr-135-adr-136-and-adr-138.md) |
| **ADR-140** | [Shipping's erasure keeps an order placed under its request (#1275), amending ADR-052](adr/ADR-140-shippings-erasure-keeps-an-order-placed-under-its-request-1275-amending-adr-052.md) |
| **ADR-141** | [Payments keeps the erasure requests an order was placed under, and its erasure spares that order (#1322), amending ADR-092](adr/ADR-141-payments-keeps-the-erasure-requests-an-order-was-placed-under-and-its-erasure-spares-that-order-1322-amending-adr-092.md) |
| **ADR-142** | [The canary's MassTransit check holds the series table to a list of verified versions; supersedes the single-version rule of ADR-047](adr/ADR-142-the-canarys-masstransit-check-holds-the-series-table-to-a-list-of-verified-versions-supersedes-the-single-version-rule-of-adr-047.md) |
| **ADR-143** | [Privacy's migrator republishes the close of each request it may announce, on ADR-090's gate (#1300), amending ADR-090 and ADR-139](adr/ADR-143-privacys-migrator-republishes-the-close-of-each-request-it-may-announce-on-adr-090s-gate-1300-amending-adr-090-and-adr-139.md) |
| **ADR-144** | [A receive endpoint switched off when its bus is configured is not configured until its switch is on (#1289), amending ADR-113](adr/ADR-144-a-receive-endpoint-switched-off-when-its-bus-is-configured-is-not-configured-until-its-switch-is-on-1289-amending-adr-113.md) |
| **ADR-145** | [A receive endpoint off at start has its topology declared by a bus that only deploys it (#1360), amending ADR-144 and ADR-026](adr/ADR-145-a-receive-endpoint-off-at-start-has-its-topology-declared-by-a-bus-that-only-deploys-it-1360-amending-adr-144-and-adr-026.md) |
| **ADR-146** | [Shipping's erasure empties a shipment's request ids before it deletes the address, in two statements of one unit (#1320), amending ADR-103](adr/ADR-146-shippings-erasure-empties-a-shipments-request-ids-before-it-deletes-the-address-in-two-statements-of-one-unit-1320-amending-adr-103.md) |
| **ADR-147** | [A scalar `Switches:Off` refuses the host at start and is reported Degraded on a reload (#1291), amending ADR-113 and ADR-115](adr/ADR-147-a-scalar-switchesoff-refuses-the-host-at-start-and-is-reported-degraded-on-a-reload-1291-amending-adr-113-and-adr-115.md) |
| **ADR-148** | [The object store check writes a sentinel with the BFF's pair and reads it back with Catalog's (#1221)](adr/ADR-148-the-object-store-check-writes-a-sentinel-with-the-bffs-pair-and-reads-it-back-with-catalogs-1221.md) |
| **ADR-149** | [The AI assistant's core and its adapter are two building blocks this repository publishes as NuGet packages (#1346)](adr/ADR-149-the-ai-assistants-core-and-its-adapter-are-two-building-blocks-this-repository-publishes-as-nuget-packages-1346.md) |
| **ADR-150** | [The assistant packages' Version rule is checked on the pull request, and their blocks leave the shared filter (#1447), amending ADR-149](adr/ADR-150-the-assistant-packages-version-rule-is-checked-on-the-pull-request-and-their-blocks-leave-the-shared-filter-1447-amending-adr-149.md) |
| **ADR-151** | [The Web BFF hosts the AI assistant for signed-in customers, off by default and scoped to the caller (#1347), amending ADR-053, ADR-149 and ADR-150](adr/ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-off-by-default-and-scoped-to-the-caller-1347-amending-adr-053-adr-149-and-adr-150.md) |
| **ADR-152** | [The assistant core sweeps idle conversations on a timer, so IdleTimeout plus a quarter bounds retention (#1457), amending ADR-151](adr/ADR-152-the-assistant-core-sweeps-idle-conversations-on-a-timer-so-idletimeout-plus-a-quarter-bounds-retention-1457-amending-adr-151.md) |
| **ADR-153** | [The deployed BFF runs the assistant on a single-replica release behind its own gateway route (#1451), amending ADR-151 and ADR-065](adr/ADR-153-the-deployed-bff-runs-the-assistant-on-a-single-replica-release-behind-its-own-gateway-route-1451-amending-adr-151-and-adr-065.md) |
| **ADR-154** | [The assistant reads whether a product is in stock through an Inventory gRPC hop under the BFF's own credentials (#1452), amending ADR-151](adr/ADR-154-the-assistant-reads-whether-a-product-is-in-stock-through-an-inventory-grpc-hop-under-the-bffs-own-credentials-1452-amending-adr-151.md) |
| **ADR-155** | [A red push run on main files a tracker issue and is reverted before it is fixed (#1450)](adr/ADR-155-a-red-push-run-on-main-files-a-tracker-issue-and-is-reverted-before-it-is-fixed-1450.md) |
| **ADR-156** | [The BFF's assistant gains a keyless scripted provider for Development, and its 429s carry a code of their own (#1484, #1485), amending ADR-151](adr/ADR-156-the-bffs-assistant-gains-a-keyless-scripted-provider-for-development-and-its-429s-carry-a-code-of-their-own-1484-1485-amending-adr-151.md) |
| **ADR-157** | [The gateway's budgets and the workers' third-party pacing are options only a stress override raises (#1488)](adr/ADR-157-the-gateways-budgets-and-the-workers-third-party-pacing-are-options-only-a-stress-override-raises-1488.md) |
| **ADR-158** | [The stress override runs Ordering.Api at its chart's replica floor (#1525)](adr/ADR-158-the-stress-override-runs-orderingapi-at-its-charts-replica-floor-1525.md) |
| **ADR-159** | [Framework categories log at Warning unless configured (#1532)](adr/ADR-159-framework-categories-log-at-warning-unless-configured-1532.md) |
| **ADR-160** | [The stress override samples traces, parent-based (#1533)](adr/ADR-160-the-stress-override-samples-traces-parent-based-1533.md) |
| **ADR-161** | [The outbox key is optimised for sequential inserts and a pass completes its rows in one statement (#1531)](adr/ADR-161-the-outbox-key-is-optimised-for-sequential-inserts-and-a-pass-completes-its-rows-in-one-statement-1531.md) |
| **ADR-162** | [The stress override bounds the observer, and the collector sheds within its bound (#1534)](adr/ADR-162-the-stress-override-bounds-the-observer-and-the-collector-sheds-within-its-bound-1534.md) |
| **ADR-163** | [The outbox dispatcher publishes Broker rows side by side and claims again at once after a full claim (#1535), adding to ADR-131](adr/ADR-163-the-outbox-dispatcher-publishes-broker-rows-side-by-side-and-claims-again-at-once-after-a-full-claim-1535-adding-to-adr-131.md) |
| **ADR-164** | [BrokerConcurrency stays at 8, the stress rerun moving no drain (#1546), adding to ADR-163](adr/ADR-164-brokerconcurrency-stays-at-8-the-stress-rerun-moving-no-drain-1546-adding-to-adr-163.md) |
| **ADR-165** | [The outbox lanes keep one claim, splitting them freeing the Broker lane but not the drain (#1559), adding to ADR-164](adr/ADR-165-the-outbox-lanes-keep-one-claim-splitting-them-freeing-the-broker-lane-but-not-the-drain-1559-adding-to-adr-164.md) |
| **ADR-166** | [The order summary projection writes and claims in one batch and one commit (#1562), adding to ADR-165](adr/ADR-166-the-order-summary-projection-writes-and-claims-in-one-batch-and-one-commit-1562-adding-to-adr-165.md) |
| **ADR-167** | [The saga outbox is clustered on its inbox-message index (#1570), adding to ADR-032 and ADR-112](adr/ADR-167-the-saga-outbox-is-clustered-on-its-inbox-message-index-1570-adding-to-adr-032-and-adr-112.md) |
| **ADR-168** | [The outbox lanes keep one claim after ADR-166 as well (#1570), adding to ADR-165 and ADR-166](adr/ADR-168-the-outbox-lanes-keep-one-claim-after-adr-166-as-well-1570-adding-to-adr-165-and-adr-166.md) |
| **ADR-169** | [InboxState's key keeps its page latching: the sequential-key option measured to move nothing (#1581), adding to ADR-167](adr/ADR-169-inboxstates-key-keeps-its-page-latching-the-sequential-key-option-measured-to-move-nothing-1581-adding-to-adr-167.md) |

---

[← §15 CI/CD](15-cicd-deployment.md) · [Index](README.md) · [Appendix B →](appendix-b-licences.md)
