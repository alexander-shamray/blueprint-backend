# Local development environment

[§14.1](../../docs/backend-architecture/14-local-development.md) is the
specification for this directory; this file records what a developer needs
at the keyboard. One command starts the infrastructure:

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
```

| Service | Host port(s) | Credentials |
|---|---|---|
| SQL Server | 1433 | `sa` / `Local_Dev_Pa55w0rd!` — override with `SQL_PASSWORD` |
| Redis (cache) | 6379 | `<service>-svc` / `local-dev-<service>` per service, in `redis/users.conf` (§8.1) |
| Redis (coordination) | 6380 | the same users |
| RabbitMQ | 5672, management http://localhost:15672 | **no login ships** — see below |
| Keycloak | http://localhost:8080 | admin/admin |
| OTel collector | 4317 (OTLP gRPC), 4318 (OTLP HTTP) | — |
| Grafana | http://localhost:3000 | — |

The credentials are development defaults, documented deliberately (§14.1);
every deployed environment takes its secrets from a vault
([§15.4](../../docs/backend-architecture/15-cicd-deployment.md)). Copy
`.env.example` to `.env` to override one.

**The broker's row is the one that changed, and the management console is the
visible cost.** Since
[ADR-036](../../docs/backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md)
each service authenticates as itself, as `<service>-svc`, whose
passwords are in that service's own file under `services/` beside every other
local default it carries — and
**`guest` is not created at all**. RabbitMQ seeds that account only when it
boots with an empty database and skips it when definitions are imported, which
`rabbitmq/20-commerce.conf` arranges. Note the consequence of a stale volume:
importing definitions does not *delete* a `guest` that already exists, so
`docker compose down -v` is what makes the removal true on a stack that ran
before this landed.

No service account carries a tag, so **nothing here can log into
http://localhost:15672**, and that is the design rather than an oversight —
`administrator` on a shared account is what
[#44](https://github.com/alexander-shamray/blueprint-backend/issues/44) was
about. Inspect the local broker through the container instead, which needs no
account at all:

```bash
docker compose exec rabbitmq rabbitmqctl list_queues name messages
docker compose exec rabbitmq rabbitmqctl list_exchanges name type
docker compose exec rabbitmq rabbitmqctl list_permissions
```

[`runbooks/error-queue.md`](../../docs/runbooks/error-queue.md) drives the
Management API rather than these, and it is written against a **deployed**
broker whose operator credential comes from the vault — not against this one.

**Every port here and in every unit below is published on `127.0.0.1` rather
than on every interface**, which is the control standing in front of those
defaults — `localhost` is what each recipe in this file already types, so the
bind costs them nothing and keeps the stack off the network the laptop is
sitting on.

## Application services

[§14.1](../../docs/backend-architecture/14-local-development.md)'s model is
one baseline and one file per deployable unit. `docker-compose.yml` is the
index, and its `include:` list names `infrastructure.yml` — the baseline the
table above covers — and each unit under `services/`, one line apiece. A
unit's first line says what it is and cites its owner, and its `ports:` lines
are where its host ports are allocated, so this lists every unit, each
container it declares and what that container publishes:

```bash
grep -E '^# |^  [a-z-]+:|ports:' deploy/compose/services/*.yml
```

A container with no `ports:` line publishes nothing, and a simulator's
scripted responses are in the README beside its mappings.

**Every OpenAPI document needs a token**, and that is a decision rather than
an oversight. `MapOpenApi()` carries no authorization metadata, so the
deny-by-default fallback
([ADR-030](../../docs/backend-architecture/adr/ADR-030-authorization-is-deny-by-default-in-the-building-block.md))
answers 401 to an anonymous request for one: the document enumerates every
route and every schema its service has, and
[§11.2](../../docs/backend-architecture/11-identity-authorization.md) assumes
the network is hostile. The path still exists and still generates — fetch it
with the token *Getting a token* below shows how to obtain:

```bash
curl -H "Authorization: Bearer $TOKEN" http://localhost:5102/openapi/v1.json
```

The health probes stay anonymous, because the kubelet carries no token
([§13.5](../../docs/backend-architecture/13-observability.md)).

The gateway is the single entry point for external clients
([§10.1](../../docs/backend-architecture/10-api-gateway.md)), so the same
listing is reachable two ways — and the two are not equivalent:

```bash
curl http://localhost:5102/v1/catalog/products     # the service, directly
curl http://localhost:5000/api/v1/catalog/products # through the gateway
```

The edge adds `/api`, which the gateway strips before forwarding, and applies
what the service does not: the rate limit, the CORS policy, and a correlation
ID. A supplied `X-Correlation-Id` is kept when it is a plausible identifier —
up to 128 characters of letters, digits, `-` and `_` — and any other value is
replaced with a fresh one rather than echoed, exactly as a missing one is
(§10.4).

A route in §10.2's file whose service is not running answers 502 on that
path and costs nothing else; the two configuration tests over the file are
what let it ship whole.

## Getting a token

PR-16 closed the gap this file used to name: publishing a product now needs a
bearer token carrying `catalog:write`. **Listing products does not, and that is
permanent** — [§10.2](../../docs/backend-architecture/10-api-gateway.md)'s
`catalog-public` route is GET-only and names `anonymous`, YARP's reserved value
for `AllowAnonymous`, so a product listing is public at the edge and public
here. It used to name no policy at all and mean the same thing; ADR-030's
fallback is what ended that, because a route saying nothing now inherits the
fallback and answers 401. The route says what it means instead — which is the
same reason the OpenAPI documents above changed and this one did not.

The realm ships two logins, both development defaults in the sense §14.1
already uses for `admin/admin` and the broker's service accounts:

| User | Password | Holds |
|---|---|---|
| `demo` | `demo` | `catalog:write`, `orders:write`, `orders:cancel`, `inventory:admin`, `payments:admin` |
| `browser` | `browser` | nothing — the account that proves a refusal |

`orders:admin` is grantable and held by **nobody**, deliberately: it overrides
§11.4's ownership check, and the 404 that hides another customer's order stays
demonstrable only while no shipped login can bypass it.

```bash
TOKEN=$(curl -s http://localhost:8080/realms/commerce/protocol/openid-connect/token \
    -d grant_type=password -d client_id=web-app \
    -d username=demo -d password=demo |
    python -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')

curl -X POST http://localhost:5102/v1/catalog/products \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"commandId":"'"$(uuidgen)"'","name":"Walnut desk","amount":19.99,"currency":"EUR"}'
```

The same call as `browser` is a 403 and the same call with no header is a 401.
Both are worth running once: they are the difference between a token being
*checked* and a token being *carried*.

Ordering takes the same token, and **every** one of its routes needs one —
there is no anonymous half, because an order belongs to somebody where a
product listing does not:

```bash
curl -X POST http://localhost:5101/v1/orders \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"commandId":"'"$(uuidgen)"'",
         "items":[{"productId":"00000000-0000-0000-0000-000000000001","quantity":1}],
         "shippingAddress":{"line1":"1 Test Street","city":"Almaty","postalCode":"050000","country":"KZ"},
         "currency":"EUR"}'
```

**`commandId` is required and this recipe did not carry it**, which is a defect
found by running the block rather than by reading it: the call answered 400
with `'Command Id' must not be empty.` `PlaceOrderCommand` opted into
[§8.5](../../docs/backend-architecture/08-caching-redis.md)'s
`IIdempotentCommand` when that behaviour landed, and a client-generated id is
what makes a retried order one order. A fresh one per *intent* — reusing it is
how a retry is recognised, so `uuidgen` belongs on the line that means "place
this order", not on the retry.

**The product id above is a placeholder and the call will answer 422 for it**
(`order.products_unavailable`): §6.6's projection fills `ordering.ProductPrices`
from Catalog's `PriceChanged`, so the id has to be one the publish call above
actually returned, and the event has to have been consumed. That is the
broker path working end to end — and, since ADR-036, Ordering consuming an
event Catalog published under a different account than its own.

**No cancel call here, deliberately, because there is no id to cancel with.**
The obvious next line — capture the response and interpolate it into
`/v1/orders/$ORDER/cancel` — is wrong twice over: this call answers with a
problem document, and a call that succeeds answers `Results.Ok(guid)`, whose
body is a JSON *string* with the quotes still on it, which `{id:guid}` cannot
bind. A reader who wants the id then needs `| jq -r .`, and a README that says
so before it can produce one is documenting a shell trick rather than the
service.

**This call answers 422 `order.products_unavailable`, and it will keep doing
so.** That is not a gap waiting on a pull request: prices come from a local
projection of Catalog's events (§6.4), and `00000000-…-0001` is an id no
Catalog event names. The projection that fills `ordering.ProductPrices` has
existed since PR-20 — a product it has never heard of has no row, no price and
no order, which is §6.6's standing consequence rather than a broken example.
Making this `curl` succeed means publishing a Catalog product and ordering
*that* id, which is two more steps than a README block earns; the reachable
proofs here stay the 401, the 403 as `browser`, and the 404 an order you do not
own returns.

Override connection strings with `<SERVICE>_CONNECTION` /
`<SERVICE>_MIGRATOR_CONNECTION` — one pair per service, every one commented out
in `.env.example` so the nested `${SQL_PASSWORD:-…}` keeps following an
overridden password rather than freezing it. They default to the `sa` login
above, and only the configuration *keys* differ locally
([§7.1](../../docs/backend-architecture/07-persistence.md),
[§14.2](../../docs/backend-architecture/14-local-development.md)).

To run the infrastructure alone — services on the host, under a debugger —
apply the override (§14.1):

```bash
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.infra-only.yml up -d --wait
```

The host process reads none of the `environment:` blocks above, so every key
Catalog and Ordering refuse to start without, which §14.1 names, has to reach
them another way. Same values, host names in place of service names, and the
coordination Redis on its published port (§14.1):

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Catalog='Server=localhost;Database=Catalog;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://catalog-svc:local-dev-catalog@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
export ConnectionStrings__RedisCache='localhost:6379,user=catalog-svc,password=local-dev-catalog'
export ConnectionStrings__RedisCoordination='localhost:6380,user=catalog-svc,password=local-dev-catalog'
# Catalog and Ordering each pin their own ports, and on the host both have to
# move. Each declares two Kestrel endpoints — 8080 for REST and a second for
# its gRPC surface, because a cleartext port cannot serve HTTP/1.1 and h2c at
# once — and 8080 on the host belongs to Keycloak, so a host run without these
# two lines fails to bind.
#
# They are the only way to move them: declaring Kestrel:Endpoints at all
# suppresses ASPNETCORE_URLS and ASPNETCORE_HTTP_PORTS entirely, measured
# against both. What still works is the same configuration key from a higher
# provider, which is what these are.
export Kestrel__Endpoints__Rest__Url='http://localhost:5102'
export Kestrel__Endpoints__Grpc__Url='http://localhost:8081'
dotnet run --project src/Services/Catalog/Catalog.Api
```

The failure without them is loud — *Failed to bind to address
http://0.0.0.0:8080: address already in use* — which is the right shape for a
clash between two things that both want a port. It is named here anyway,
because the address it names is Keycloak's and the project it names is the
one being run, and nothing in that message says the two are related.

Ordering is the same shape with its own key — `ConnectionStrings__Ordering`,
never Catalog's, because `AddOrderingInfrastructure` reads its own name and
`AddSqlServer` throws without it:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Ordering='Server=localhost;Database=Ordering;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://ordering-svc:local-dev-ordering@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
export ConnectionStrings__RedisCache='localhost:6379,user=ordering-svc,password=local-dev-ordering'
export ConnectionStrings__RedisCoordination='localhost:6380,user=ordering-svc,password=local-dev-ordering'
# The same two exports Catalog needs, for the same reason and at its own
# numbers: 5101 is the port §14.1 already allocates this service, and 8082
# rather than 8081 because 8081 is where the block above puts Catalog's h2c
# listener — two host processes cannot both hold it.
export Kestrel__Endpoints__Rest__Url='http://localhost:5101'
export Kestrel__Endpoints__Grpc__Url='http://localhost:8082'
dotnet run --project src/Services/Ordering/Ordering.Api
```

`ASPNETCORE_ENVIRONMENT` is the first line for a reason: no project here ships
a `launchSettings.json`, so `dotnet run` is Production unless something says
otherwise, and `AddJwtAuthentication` refuses a plain-HTTP authority outside
Development (§11.3), so the host does not start. The container sets the same
variable, which is why the Compose path never shows this.

The override excludes the `gateway` too, and running that one on the host takes
one more variable: §10.2's destinations are container names, which resolve on
the Compose network and nowhere else, so a host-run gateway has to be told
where the service actually is.

```bash
export ASPNETCORE_ENVIRONMENT=Development
export Identity__Authority='http://localhost:8080/realms/commerce'
export ReverseProxy__Clusters__catalog__Destinations__d1__Address='http://localhost:5102/'
export ReverseProxy__Clusters__ordering__Destinations__d1__Address='http://localhost:5101/'
export ReverseProxy__Clusters__web-bff__Destinations__d1__Address='http://localhost:5200/'
export ReverseProxy__Clusters__inventory__Destinations__d1__Address='http://localhost:5103/'
export ReverseProxy__Clusters__payments__Destinations__d1__Address='http://localhost:5104/'
dotnet run --project src/Gateway/Gateway.Api
```

**A destination joins this block with the PR that builds its service**, the
same rule the Compose file's `depends_on` follows. Ordering's line arrived with
PR-18 and the BFF's with PR-19; without one a host-run gateway 502s the exact
path the PR exists to stop answering 502.

The BFF is excluded too, and it needs more than an authority — its
projection's database (ADR-051), which the override leaves running and the
excluded `bff-migrator` would have migrated, so run the migrator first with
`ConnectionStrings__BffMigrator` set to the same value; the broker that
feeds it, under the BFF's own account (ADR-036); §15.4's three
`Identity__Client__*` rows are required of a host that calls a peer,
`ValidateOnStart` refuses to boot without all three, and its own hop needs
Catalog's **gRPC** port rather than its REST one:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Bff='Server=localhost;Database=Bff;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://bff-svc:local-dev-bff@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
export Identity__Client__ClientId='web-bff'
export Identity__Client__ClientSecret='local-dev-secret'
export Identity__Client__Scope='commerce-api'
dotnet run --project src/BFF/Web.Bff
```

**That block leaves the hop pointed at `catalog-api:8081`, which resolves on
the Compose network and nowhere else**, so a host-run BFF answers 503 on
`/v1/checkout/quote` until Catalog is reachable under that name. The address is
a literal rather than a configuration key on purpose (§9.7): it is the same
string in Compose and in Kubernetes, and §15.4's rule is that a value which
does not vary is not configuration. A `hosts` entry mapping `catalog-api` to
`127.0.0.1` is the honest local workaround — and note that a host-run
`Catalog.Api` does listen on 8081, because its `appsettings.json` declares both
endpoints and that file overrides `ASPNETCORE_HTTP_PORTS`.

Payments refuses to start without its provider too, read as eagerly as the
authority (§15.4). The override leaves `psp-simulator` running, so a host-run
Payments points at the port it publishes, with the same local key the Compose
unit sets:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Payments='Server=localhost;Database=Payments;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://payments-svc:local-dev-payments@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
export PaymentProvider__BaseUrl='http://localhost:5190/'
export PaymentProvider__ApiKey='local-dev-psp'
dotnet run --project src/Services/Payments/Payments.Api
```

`ASPNETCORE_ENVIRONMENT` leads this block for the same reason it leads the one
above, and the block is written to stand alone rather than as a delta on that
shell: without it the authority on the next line is plain HTTP outside
Development, `AddJwtAuthentication` refuses it at startup, and the host does
not run at all. Every host here validates tokens (§11.2), so every host-run
block that names an authority needs this line — and the migrator below does
not, because its job never sees a token. **That is the rule and deliberately
not a count**: this sentence said "both of them" until Ordering's block made
three, which is the same way the compose smoke's image count went stale, one
file over.

Shipping's worker refuses to start without `Carrier__BaseUrl`,
`Carrier__ApiKey` and `AddressSource__BaseUrl`, read as eagerly as the
authority, or without the two `Jurisdiction__*` retention windows and
`Fulfilment__GiveUpAge`, validated at start (§15.4) — and it calls a peer, so it
takes the three
`Identity__Client__*` keys as the BFF does
([ADR-052](../../docs/backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).
The windows are the made-up ones the Compose unit sets, because a developer's
machine is no jurisdiction and a real window is a deployment's value
([ADR-053](../../docs/backend-architecture/adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)
rule 1). The override leaves `carrier-simulator` running, so a host-run worker
points at the port it publishes:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Shipping='Server=localhost;Database=Shipping;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://shipping-svc:local-dev-shipping@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
export Identity__Client__ClientId='shipping-worker'
export Identity__Client__ClientSecret='local-dev-shipping-secret'
export Identity__Client__Scope='commerce-api'
export Carrier__BaseUrl='http://localhost:5191/'
export Carrier__ApiKey='local-dev-carrier'
export AddressSource__BaseUrl='http://localhost:8082'
export Jurisdiction__AddressRetention='11.00:00:00'
export Jurisdiction__TrackingRetention='23.00:00:00'
export Fulfilment__GiveUpAge='3.00:00:00'
dotnet run --project src/Services/Shipping/Shipping.Worker
```

**This hop is not the BFF's.** That block leaves its address on the Compose
network because §9.7 makes the pricing hop's address a literal rather than a
key; this one is a configuration key (§15.4), so a host-run worker points it
at a host-run Ordering and needs no `hosts` entry. `AddressSource__BaseUrl` is
the h2c endpoint Ordering's block above moves off Catalog's, because two host
processes cannot hold one port.

`Cors__Enabled` and `Ingress__Enabled` are both absent above and both default
to off, which is the shape the flags are written for — off is a valid
topology, on-but-unconfigured is not, and the host refuses to start in the
second state rather than starting into it.

The override excludes `catalog-migrator` as well as `catalog-api`, so the
schema is nobody's job until it is run — on the host, under the *other*
connection string, because §7.1 keeps the two identities apart even where the
local login is one:

```bash
export ConnectionStrings__CatalogMigrator='Server=localhost;Database=Catalog;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
dotnet run --project src/Services/Catalog/Catalog.Migrator
```
