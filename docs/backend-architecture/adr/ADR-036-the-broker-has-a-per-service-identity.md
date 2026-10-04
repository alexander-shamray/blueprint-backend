# ADR-036 — The broker has a per-service identity

**Decision.** Every service authenticates to RabbitMQ as its own account, and
that account's `write` permission is scoped to the exchanges its own source
addresses: its bounded context's contracts, the receive endpoints it hosts, the
peer command endpoints its `Endpoints` class names, and the framework's fault
exchanges. The accounts and their permissions are declared in
`deploy/compose/rabbitmq/definitions.json`, imported at broker start, and held
to the code by `check_permissions.py`. `guest` is not among them.

**Why.** [§9.4](../09-messaging.md) stamps a broker-borne command
`CommandOrigin.System` because it arrived on the service's own command queue,
and [§11.4](../11-identity-authorization.md)'s ownership check is skipped for a
system-initiated command. That made queue write access sufficient to execute a
business command with owner privileges — confirm an order that was never paid
for, cancel any customer's order, mark one shipped with a forged tracking
number — and the platform had exactly one broker principal for every service to
share.

It was worse than "shared". Measured on the base image rather than assumed:
`guest` is tagged `administrator`, and `rabbitmq:4.1-management-alpine` ships
`loopback_users.guest = false` under the comment *"allow access to the guest
user from anywhere on the network"*. `rabbitmqctl environment` reported
`{loopback_users,[]}` and both services connected from container addresses. So
the least valuable service in the estate — [§3.2](../03-bounded-contexts.md)'s
Notifications, which publishes nothing — would have been enough to drive
Ordering's write model.

[§11.2](../11-identity-authorization.md) already states the posture this applies:
*"anything that reaches a service by another path would otherwise be
unauthenticated. Validation is cheap; assume the network is hostile."* That
reasoning had been applied to HTTP and never to the transport carrying
commands. [§8.1](../08-caching-redis.md)'s per-service Redis ACL is the same
control one component over, and its argument — *"the ACL makes this impossible
rather than discouraged"* — is the one taken here.

**The alternative was a signed envelope, and it is not what this closes.** #44
proposed either broker isolation or a verifiable origin header; §9.4 forbids
the cheap version of the second outright, because an origin a caller can set is
an origin a caller can forge. A real one needs message signing, key
distribution and rotation that no chapter specifies — and it would buy nothing
here, because `ordering-commands` has exactly one producer today: Ordering's
own saga. Restricting write to that identity *is* the second half, reached by
the first half's mechanism.

**Consequences.** Three costs, all taken deliberately and none of them hidden.

`configure` is not exclusive. A consumer declares the exchange it binds, so
Ordering's `configure` reaches every `Common.Contracts.*` exchange including
Catalog's, and it could delete one it does not own. A topology where producers
pre-declare and consumers only bind would close it; MassTransit does not offer
one.

`read` on a peer's command endpoint is unavoidable, and it is wider than it
looks. A send to `queue:inventory-commands` declares and binds that
destination, `queue.bind` takes `read` on the exchange — measured, as a refusal
with `write` already granted — and a RabbitMQ permission pattern cannot
distinguish a queue from an exchange of the same name. So granting Ordering the
bind grants it the ability to consume Inventory's commands. The property this
ADR claims is about `write`, and that limit is stated rather than papered over.

**Provisioning is an obligation on a deployed broker, not a guarantee.** The
Helm charts name one Secret per service — `catalog-rabbitmq`,
`ordering-rabbitmq` — and this repository holds no configuration for the broker
those Secrets point at and runs no deploy-time check against it. That is the
division [§15.4](../15-cicd-deployment.md) already draws for every Secret and
[ADR-033](ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md)
draws for the realm: verified where the platform provisions its own
infrastructure, stated as an obligation where it does not.

What is verified is local, and **which half `dotnet test` verifies is worth
stating exactly, because it is not all of it.** Both Testcontainers fixtures
run a broker holding the same imported permissions the Compose one does, so
the suites run against real permissions rather than against a broker with
none: a grant too narrow to declare a receive endpoint or bind a peer queue
fails on the branch that narrowed it. That is the half that rots as endpoints
are added.

**They get there by two different routes, and saying they both build the
image was wrong.** Ordering's fixture builds it, because ADR-021's
delayed-exchange plugin leaves it no choice. Catalog's maps
`definitions.json` and `20-commerce.conf` onto the stock image instead: it
runs no saga, so the plugin buys it nothing, and building put its two test
hosts — `Catalog.Api.Tests` and `Catalog.Application.Tests` — in a race for
one Testcontainers build-context tar. The mapped paths are a second copy of
the Dockerfile's `COPY` targets and `check_permissions.py` holds the two to
each other, because a drifted mapping boots a broker with no definitions,
seeds `guest`, and passes.

**The negative half is not exercised by that suite, and cannot be.** §9.6's saga
is driven by events Inventory, Payments, Shipping and Catalog publish, and none
of those services exists — so `Ordering.Api.Tests` publishes them through the
host's own bus, as `ordering-svc`, which ADR-036's production permissions
correctly refuse. The harness therefore widens its own `write` at fixture
start, in test code and never in `definitions.json`: loosening the deployed
artefact so a test could pass would leave the gate agreeing with a permission
set nothing deploys. It shrinks to nothing as each of those services gains a
publisher of its own.

So the negative property rests on two other things, both of which are real. It
was **measured**: attempted as `catalog-svc` against a running broker —
`ConfirmOrder` onto `ordering-commands`, a forged `OrderPlaced`, a
`ReserveStock`, a forged `PaymentAuthorised` — and refused on all four, with a
positive control publishing Catalog's own contract to prove the credential
worked. And it is **gated**: `check_permissions.py` derives what each service
needs from its own source and asserts that no service may write another's, so a
new receive endpoint, a new peer queue or a sixth bounded context fails the
build rather than the deployment.

> **The BFF holds an account under this record, and nothing here has been
> edited.** [ADR-051](ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)
> gives `Web.Bff` consumers, so it authenticates as `bff-svc`, which writes
> its own `bff-` endpoints and the fault exchanges and no contract: it
> publishes nothing, the case this record argued from Notifications.
> `check_permissions.py` reads the host's `Messaging` directory by the same
> derivation as a service's, so the account is held to its source as theirs
> are.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
