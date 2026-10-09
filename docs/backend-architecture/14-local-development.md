# 14. Local development

## 14.1 Docker Compose — the baseline

One command starts the platform. This is the documented default; it requires
only Docker and works identically on every operating system and in CI.

The Compose file is an index and one file per deployable unit, so a service's
environment is a file that service's PR owns rather than a block in a file
every service PR edits — [`docs/change-locality.md`](../change-locality.md)'s
rule applied to the one deployment artefact every service has to touch. The
index is `deploy/compose/docker-compose.yml`, and it declares no service of
its own:

```yaml
name: commerce

include:
  - infrastructure.yml
  - services/catalog.yml
  - services/gateway.yml
  - services/ordering.yml
  - services/web-bff.yml
  - services/inventory.yml
  - services/payments.yml
  - services/shipping.yml
  - services/notifications.yml
```

**`include` is what raises the floor, and it is the one prerequisite this
section has.** Compose gained the top-level element in **v2.20.0**; an older
v2 rejects this model before anything starts, which is a failure at parse time
rather than a service that misbehaves. Docker Desktop and a current
`docker-compose-plugin` are both well past it, so "only Docker" holds — the
version is stated because a floor nobody writes down is one a stale
installation discovers.

`include` resolves a relative path against the directory of the file that
declares it rather than against the index, so a unit under `services/` spells
its build context one level deeper than the index would; interpolation does not
follow that rule, and the `.env` beside the index reaches every included file.
Both were measured with `docker compose config` rather than assumed, because a
bind mount resolving to the wrong directory is a container that starts and
reads nothing.

What Compose reads is the model those files make between them, and the rest
of this section specifies it. Each excerpt below is from the file it names,
which holds the rest.

`deploy/compose/infrastructure.yml` is the shared baseline: SQL Server, the two
Redis instances [§8.1](08-caching-redis.md) keeps apart because eviction policy
cannot be shared, the broker, Keycloak, the OpenTelemetry collector and
Grafana, with the volumes that outlive a `down`. It pins every image's tag.
Both Redis instances read `deploy/compose/redis/users.conf`, §8.1's
per-service ACL users: each service's user reaches its own prefix only, and the
default user may `PING` and nothing else. The cache evicts `allkeys-lru`; the
coordination instance runs `noeviction`, because locks and idempotency keys
must never be evicted, and `appendonly`, so a restart does not silently release
held locks.

**The broker is built rather than pulled, and it is the one infrastructure
service that is.** `deploy/compose/rabbitmq/Dockerfile` adds the delayed
message exchange plugin §9.6's saga timeouts are scheduled through
([ADR-021](adr/ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)), pinned
by digest; the base image is an official tag, one line inside it. A broker
missing the plugin is running and healthy while every saga schedule hangs on a
declare it refuses, so the healthcheck asks for the plugin as well as the
broker:

```yaml
rabbitmq:
  build:
    context: rabbitmq
  ports: [ "127.0.0.1:5672:5672", "127.0.0.1:15672:15672" ]
  volumes: [ rabbit-data:/var/lib/rabbitmq ]
  healthcheck:
    test: ["CMD-SHELL", "rabbitmq-diagnostics check_running && rabbitmq-plugins is_enabled rabbitmq_delayed_message_exchange"]
    interval: 10s
    retries: 5
```

`is_enabled` reports its answer in its exit status, and that is the only part
of the command that does. `rabbitmq-plugins list -e` piped to `grep` matches
the pattern the command echoes in its own banner, and passes on a stock broker.

**Keycloak has one issuer, whichever host asks.** Without `KC_HOSTNAME` it
derives the issuer from each request's `Host` header, so a token minted through
`localhost:8080` and a discovery document read through `keycloak:8080`
disagree — and `ValidateIssuer` ([§11.3](11-identity-authorization.md)) rejects
every token obtained the way this chapter documents.
`KC_HOSTNAME_BACKCHANNEL_DYNAMIC` puts the backchannel back on the container
route, which is the half the services need:

```yaml
keycloak:
  command: start-dev --import-realm
  environment:
    KC_BOOTSTRAP_ADMIN_USERNAME: admin
    KC_BOOTSTRAP_ADMIN_PASSWORD: admin
    KC_HOSTNAME: http://localhost:8080
    KC_HOSTNAME_BACKCHANNEL_DYNAMIC: "true"
    KC_HEALTH_ENABLED: "true"
```

`KC_HEALTH_ENABLED` turns on `/health/ready`, on the management port, and the
healthcheck reads it through a bash TCP redirection: the image has a shell but
no HTTP client, and that is the form Keycloak's own documentation gives.
Without a healthcheck `up --wait` returns when the process launches rather
than when the realm is importable, and the first token request races the
import.

**The pair rule: every service's unit is a pair**, a `{service}-migrator`
one-shot and the service itself, gated on
`condition: service_completed_successfully`. Every service owns a database and
none may migrate at startup ([§4.1](04-solution-structure.md),
[ADR-007](adr/ADR-007-migrations-as-a-pre-deploy-job.md)), so a service added
without its migrator starts against an empty schema.
`deploy/compose/services/ordering.yml` shows the pair; every other service's
unit keeps it, beside whatever else that service needs:

```yaml
ordering-migrator:
  build:
    context: ../../..
    dockerfile: src/Services/Ordering/Ordering.Migrator/Dockerfile
  read_only: true
  tmpfs: [ /tmp ]
  environment:
    ConnectionStrings__OrderingMigrator: "${ORDERING_MIGRATOR_CONNECTION:-Server=sql;Database=Ordering;User Id=sa;Password=${SQL_PASSWORD:-Local_Dev_Pa55w0rd!};TrustServerCertificate=True}"
    DOTNET_ENVIRONMENT: Development
  depends_on:
    sql: { condition: service_healthy }
  restart: "no"

ordering-api:
  build:
    context: ../../..
    dockerfile: src/Services/Ordering/Ordering.Api/Dockerfile
  read_only: true
  tmpfs: [ /tmp ]
  environment:
    ASPNETCORE_ENVIRONMENT: Development
    ConnectionStrings__Ordering: "${ORDERING_CONNECTION:-Server=sql;Database=Ordering;User Id=sa;Password=${SQL_PASSWORD:-Local_Dev_Pa55w0rd!};TrustServerCertificate=True}"
    ConnectionStrings__RabbitMq: "amqp://ordering-svc:local-dev-ordering@rabbitmq:5672"
    Identity__Authority: "http://keycloak:8080/realms/commerce"
    ConnectionStrings__RedisCache: "redis-cache:6379,user=ordering-svc,password=local-dev-ordering"
    ConnectionStrings__RedisCoordination: "redis-coordination:6379,user=ordering-svc,password=local-dev-ordering"
  ports: [ "127.0.0.1:5101:8080" ]
  healthcheck:
    test: [ "CMD", "dotnet", "Ordering.Api.dll", "--probe" ]
  depends_on:
    ordering-migrator: { condition: service_completed_successfully }
    rabbitmq: { condition: service_healthy }
    keycloak: { condition: service_healthy }
    redis-cache: { condition: service_healthy }
    redis-coordination: { condition: service_healthy }
```

Every container a unit builds from `src/` — each migrator and each host — runs
read-only, with a tmpfs at `/tmp`, as the chart's pods do
([ADR-082](adr/ADR-082-every-pod-meets-pod-security-restricted-with-a-read-only-root-filesystem.md)),
so the Compose workflow's `up --wait` proves the images run that way.

The migrator reads §7.1's migrator key, which may issue DDL, and the API reads
the runtime key and never the migrator's. Locally both resolve to the one `sa`
login (§14.2's stated simplification); in production they are separate secrets
on separate workloads. Each default is inline, nesting the password's own
default, because `.env.example` promises every variable a working default: a
bare `${…}` interpolates to an empty string on a clean checkout, which
`config -q` accepts and the first query does not. The migrator sets
`DOTNET_ENVIRONMENT` because the job host reads it and defaults to
`Production`, where
[ADR-079](adr/ADR-079-outside-development-an-infrastructure-connection-is-encrypted-unless-the-deployer-names-it-plaintext.md)
refuses the string's `TrustServerCertificate=True`.

The API carries the authority, to validate inbound tokens
([§11.2](11-identity-authorization.md)), and no `Identity__Client__*`: Ordering
calls no peer synchronously — prices come from a local projection (§6.4) and
the rest goes over the broker. [§15.4](15-cicd-deployment.md) says which hosts
hold client credentials (§11.5, ADR-052). An environment variable nothing reads
is the container form of an unused registration, so each joins a unit with the
change whose code first reads it. The unit carries both Redis connections
because `AddRedisConnections` is a single call by design (§8.2) and reads both
eagerly, so a host given one key throws naming the other.

The API waits on both Redis instances with `service_healthy`, not
`service_started`. `AbortOnConnectFail` is false, so the host starts with Redis
down (§8.1's degrade, don't die): the cache falls back to the database and
coordination callers fail closed. The host would therefore start against a
Redis still booting, and the first protected command would fail on a claim
instead — a symptom nothing connects to a container that was not ready. It
waits on Keycloak with `service_healthy` because Keycloak declares a
healthcheck; the API does not need it, since JwtBearer fetches the discovery
document lazily.

`deploy/compose/services/gateway.yml` has no migrator, because the gateway owns
no database. It carries the authority, because the gateway validates JWTs like
every other host (§11.2), and no `Identity__Client__*`, because those are for
calling other services (§11.5) and the gateway calls nobody: YARP forwards the
caller's token. `Ingress__Enabled` is `"false"`: locally the gateway is the
edge, nothing forwards, `RemoteIpAddress` is already the client, and trusting
`X-Forwarded-For` would let any caller pick its own rate-limit bucket. In
Kubernetes it is true ([§15.3](15-cicd-deployment.md)). CORS is enabled because
browsers reach the gateway directly here, and the SPA's development origin is
5173, Vite's default: 3000 belongs to Grafana, in `infrastructure.yml`.

`deploy/compose/services/web-bff.yml` pairs the BFF with `bff-migrator`, for
[ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)'s
projection. The BFF carries client credentials, because it calls a peer
synchronously ([§9.7](09-messaging.md)); §15.4 says which other hosts do
(ADR-052). `ValidateOnStart` requires them (§15.4), so the host refuses to boot
without them; the unit's values are local ones, and production mounts a
secret. The unit is named `web-bff`, matching the Aspire resource (§14.2) and
the YARP destination ([§10.2](10-api-gateway.md)): the gateway resolves the
destination by hostname, so the container name is the routing configuration.

Each application unit lands with the change that builds its image: the
scaffold of [§4.5](04-solution-structure.md) writes the service's own unit file
and the one line that includes it, along with both of its `infra-only`
exclusions below and its `.env.example` variables.

The realm file, `deploy/compose/keycloak/realm-export.json`, is a full Keycloak
export: it holds the `commerce-api` client scope with its audience and
permission mappers, the `commerce-api` client holding the permission vocabulary
as client roles, a browser client, and two development logins. **A full export
rather than a readable summary, and [§11.5](11-identity-authorization.md)
argues why** — Keycloak treats a `clientScopes` array as the complete set, so a
trimmed file silently drops the built-in scopes and with them `sub`.

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
```

`--wait` returns once each host is ready, not once it has started: every
application unit's healthcheck runs the host's own `HealthProbe`, which asks
its [§13.5](13-observability.md) readiness endpoint over loopback, and a unit
that waits on a peer with `service_healthy` waits on that same answer.

| Endpoint | URL |
|---|---|
| Gateway | http://localhost:5000 |
| Keycloak | http://localhost:8080 (admin/admin) |
| RabbitMQ management | http://localhost:15672 — **no login ships**; see below |
| Grafana | http://localhost:3000 |
| Mail sink (Mailpit) | http://localhost:8025 — every message the stack sends; no login |
| Mail relay (SMTP) | `localhost:1025` — plain and unauthenticated, Development's alone |

> **Every published port binds `127.0.0.1`, not `0.0.0.0`.** The credentials
> in the table above are development defaults on purpose, which makes the
> interface the control standing in front of them — Compose's short syntax
> with no host-IP prefix publishes on every interface, so `docker compose up`
> on a café or office network offers `sa`, every service's Redis user,
> the realm's service accounts and Keycloak's admin console to every peer on
> it. Every URL in the table is already a `localhost` one, so the prefix takes
> nothing away from the workflow this chapter documents.

The realm's own logins are `demo/demo`, which holds every permission a shipped
endpoint requires — `deploy/compose/README.md` carries the current list rather
than this sentence, because a subset named here goes stale with each service —
and `browser/browser`, which holds nothing and is the account a refusal is
proved against. `orders:admin` is grantable and held by neither, deliberately:
it overrides §11.4's ownership check, which stays demonstrable only while no
shipped login can bypass it. Both are development defaults on the same terms as
the credentials the callout above names — the deliberate local-development
exception to §11.6, which every deployed environment replaces with real users
out of a directory.

`deploy/compose/README.md` is the keyboard inventory of what runs — every port
and credential of the seven infrastructure services, beside the file it
describes, and the command that lists what each application unit publishes.

The gateway's own unit takes no `depends_on` on a service it routes to except
the ones that exist — Compose rejects a dependency it cannot see, and one
undefined name fails the whole `up` rather than one service. Its *routes* are
under no such constraint and [§10.2](10-api-gateway.md) ships all four, so
a path answers 502 while its service is not running. A route is configuration
the gateway reads; a `depends_on` is a name Compose has to resolve. Which
destinations Compose can see is the `include` list in
`deploy/compose/docker-compose.yml`, the owner of that fact, and the
`depends_on` list in `deploy/compose/services/gateway.yml` owns which of them
the gateway waits on: a destination joins it with the change that builds its
service.

**The BFF's unit takes no `depends_on` on `catalog-api`, though it calls it.**
Its pricing hop is made per request and is outside its readiness set (§13.5),
and §14.3's start order runs the dependency the other way. The hop is over
`catalog-api:8081` — a second, HTTP/2-only Kestrel endpoint, because a
cleartext port cannot serve HTTP/1.1 and h2c at once — and that port is
published to no host and reached by no route.

**Notifications' relay is a sink, and both its ports bind loopback alone.**
Mailpit runs in Notifications' own unit beside the worker that submits to it,
which reaches it as `mailpit:1025` on the Compose network. Its SMTP port and
its web UI and HTTP API are published on `127.0.0.1:1025` and
`127.0.0.1:8025`, the endpoint table's two rows: the UI because it shows
every message the stack has sent to whoever reaches the port, and SMTP so a
worker run on the host under the override below has a relay, at
`Mail__Host` `localhost`. The relay's local defaults are `Mail__Host`
`mailpit`, `Mail__Port` `1025` and `Mail__Security` `None`, with no
`Mail__UserName` and no `Mail__Password`: the sink takes unauthenticated
submission, and plain, unauthenticated submission is Development's alone —
the host refuses either anywhere else ([§15.4](15-cicd-deployment.md)). The
image's tag is the unit file's, and the suite starts the same one, so the
sink a person watches is the sink the tests read.

The collector's mounted configuration, `deploy/compose/otel/config.yaml`, is
the smallest correct pipeline — OTLP in on both protocols, a batch processor,
OTLP out to the LGTM container, which ingests OTLP directly. Every unit points
its API or worker host at it with
`OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"`. Its receivers and
its exporter, with the processors between them and the pipelines after them in
the file:

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

exporters:
  otlphttp:
    endpoint: http://grafana:4318
```

The LGTM container loads §13.6's loaded rule file into the Prometheus it
bundles and §13.8's dashboards into Grafana at start, so a rule can be seen
to fire on this stack; `deploy/observability/README.md` owns which file is
left out and `deploy/compose/README.md` the read that lists what loaded. The
bundled Prometheus takes no rule-file flag or variable, so the `grafana`
service's command in `infrastructure.yml` adds the `rule_files` key to that
Prometheus's own configuration at start, guarded so a restart does not add it
twice. Its healthcheck reads the `/tmp/ready` file `run-all.sh` touches once
every component is up, and the command removes that file first, so a restart
is not read healthy from the last run.

An override file runs infrastructure in containers while services run on the
host with a debugger attached — the usual inner-loop compromise:

```bash
# Both from the repository root — the two commands share one working
# directory, or the pair cannot be pasted as written.
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.infra-only.yml up -d
dotnet run --project src/Services/Ordering/Ordering.Api
```

**A host process reads none of the container `environment:` blocks, so every
key a service throws without has to be supplied to it.** **Five** do that, each
named by the registration that reads it — `ConnectionStrings__<Service>`
(§7.1's runtime key, `AddSqlServer`), `ConnectionStrings__RabbitMq` (§9's
bus), `Identity__Authority` ([§11.3](11-identity-authorization.md), read
eagerly by `AddJwtAuthentication`), and the two Redis connections
([§8.1](08-caching-redis.md), read eagerly by `AddRedisConnections`). The
values differ from the container ones only in the host name and, for the
second Redis instance, the port — because the compose file publishes each one:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Ordering='Server=localhost;Database=Ordering;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://ordering-svc:local-dev-ordering@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
export ConnectionStrings__RedisCache='localhost:6379,user=ordering-svc,password=local-dev-ordering'
export ConnectionStrings__RedisCoordination='localhost:6380,user=ordering-svc,password=local-dev-ordering'
# Not a key the host throws without, but a bind it fails: Ordering's
# appsettings.json pins 8080 and 8081 (ADR-052), and 8080 on the host is
# Keycloak's. deploy/compose/README.md gives Catalog's pair beside these.
export Kestrel__Endpoints__Rest__Url='http://localhost:5101'
export Kestrel__Endpoints__Grpc__Url='http://localhost:8082'
```

> **The two Redis ports differ here and are identical in the compose blocks,
> and getting that backwards is a connection to the wrong instance rather than
> an error.** Both containers listen on Redis's own 6379, so inside the network
> the addresses differ only by host name; the published ports are 6379 and
> **6380**, so on the host they differ only by port. A block copied from the
> compose file would point both connections at the cache — the same defect the
> chart's key guard refuses at render time (§15.3), arriving where nothing
> checks it.

**The key is the service's own**, and the block above is `Ordering.Api`'s
because that is the host the fence names. `AddOrderingInfrastructure` reads
`ConnectionStrings:Ordering` and `AddSqlServer` throws without it, so
exporting Catalog's key here is a set of instructions that cannot start the
process it precedes. Running the migrator instead takes
`ConnectionStrings__OrderingMigrator`, which is §7.1's whole point: two keys,
one of which may issue DDL.

**The environment is the first line and is not decoration.** No project ships a
`launchSettings.json`, so `dotnet run` is Production unless told otherwise, and
`RequireHttpsMetadata` is on in Production
([§11.3](11-identity-authorization.md)) — against a plain-HTTP local authority
the host will not fetch the discovery document at all, and every bearer request
fails before validation starts. The containers set the same variable, which is
why only the host path shows this.

The override excludes each service's **migrator** beside its API, so the schema
is the host's job too — under §7.4's separate key
(`ConnectionStrings__<Service>Migrator`), which is the one place the two
identities stay apart locally even though the login does not (§7.1).

No `appsettings.Development.json` carries these instead, and that is the same
decision §15.4 makes everywhere else: a file in the repository holding a
connection string is a credential in the repository, and a default authority
baked into configuration is one a deployed host inherits when its own key is
missing — the failure the eager throw exists to make loud.

The override's whole content, in `deploy/compose/docker-compose.infra-only.yml`,
is a profile per application service. An override cannot delete a service, but
profiles gate activation and nothing activates this one, so the default `up`
skips every service it names — declaratively, with no duplicated configuration
to drift. Ordering's two entries:

```yaml
services:
  ordering-migrator:
    profiles: [ "excluded" ]
  ordering-api:
    profiles: [ "excluded" ]
```

Every unit the index includes joins this list in the same change that adds it,
and an omitted one silently keeps starting. This is the one Compose file the
per-unit split leaves shared, because an override merges over a resolved model
and cannot be divided the way the model is. The §4.5 scaffold writes both
halves, which is the reliable way to keep a rule whose only symptom is a
container nobody asked for.

## 14.2 Aspire — optional accelerator

Aspire (MIT, Microsoft) replaces the Compose file with a C# program that starts
containers *and* your projects, injects connection strings and service discovery
automatically, and ships an OpenTelemetry dashboard with distributed tracing
already wired.

The practical difference is the inner loop: Compose containerises your services,
so debugging seven of them at once is awkward. Aspire runs your projects as host
processes while containerising only the infrastructure, so a single F5 gives
breakpoints across every service simultaneously.

```csharp
// src/AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// Resource names ARE connection-string names: WithReference(x) injects
// ConnectionStrings__{x.Name}. They must match the keys the code reads
// (§4.2, §8.2) exactly — configuration is case-insensitive but not
// punctuation-insensitive, so "redis-cache" would not satisfy
// GetConnectionString("RedisCache") and both Redis connections would be null.
var sql = builder.AddSqlServer("sql").WithDataVolume();

// Two Redis resources, mirroring §8.1 — the eviction policies are
// incompatible, so a single instance would silently evict held locks.
var cache = builder
    .AddRedis("RedisCache")
    .WithRedisCommander();
var coordination = builder
    .AddRedis("RedisCoordination")
    .WithDataVolume()       // locks must survive a restart
    .WithPersistence();

// The stock image, which is a STATED GAP rather than a parity with §14.1
// (ADR-021). Ordering's saga schedules through the delayed message exchange,
// which is a community plugin no official image carries — so this line brings
// up a broker that takes UseDelayedMessageScheduler, connects, reports
// healthy, and then hangs on the first saga schedule. §14.1 builds
// deploy/compose/rabbitmq for exactly that reason.
//
// It is left as the stock call because Aspire is not adopted (ADR-011) and a
// sample nobody compiles is the wrong place to invent an image-build API.
// **Adopting Aspire means closing this first**: point the resource at the
// same Dockerfile, or run Ordering against Compose. Catalog's test fixture
// may stay on the base tag and says why — it schedules nothing — and this
// AppHost runs Ordering, so it does not have that excuse.
var mq = builder.AddRabbitMQ("RabbitMq").WithManagementPlugin();

// One database per service or host that this AppHost runs. The rest are omitted
// deliberately — adding a database without the service and migrator
// resources that own it creates a schema nothing maintains, which is the
// shape §4.1 rules out.
var orderingDb = sql.AddDatabase("Ordering");
var catalogDb = sql.AddDatabase("Catalog");
var bffDb = sql.AddDatabase("Bff");

var keycloak = builder
    .AddKeycloak("keycloak", 8080)
    .WithRealmImport("./keycloak/realm-export.json");

// ReferenceExpression, not string concatenation: GetEndpoint() returns a
// deferred reference — the port is not allocated yet. Concatenating it with +
// would stringify the object and write a placeholder into the environment.
var authority = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/commerce");

// Every host validates JWTs (§11.2), so every host needs the authority.
// Applied by one helper rather than repeated per resource, so none is left
// without it.
//
// Client credentials are a SEPARATE concern with a narrower audience: only a
// host that makes a synchronous call under a grant of its own (§11.5) presents
// them. Passing a clientId to a host that makes no outbound call provisions a
// Keycloak client, prompts for a secret and mounts it, all for credentials
// nothing ever sends.
IResourceBuilder<ProjectResource> WithPlatformIdentity(
    IResourceBuilder<ProjectResource> project,
    string? callerClientId = null)
{
    project = project
        .WithEnvironment("Identity__Authority", authority)
        .WaitFor(keycloak);

    if (callerClientId is null)
        return project;

    return project
        .WithEnvironment("Identity__Client__ClientId", callerClientId)
        // One secret per client, not one shared across all of them: Keycloak
        // issues distinct credentials, and a shared secret would let any
        // service present itself as any other (§11.5). Prompted once and
        // stored in user secrets; in Kubernetes each is its own Secret (§15.4).
        .WithEnvironment(
            "Identity__Client__ClientSecret",
            builder.AddParameter($"{callerClientId}-secret", secret: true))
        .WithEnvironment("Identity__Client__Scope", "commerce-api");
}

// Migrations run as a job here exactly as they do in Compose and Helm —
// ADR-007 forbids migrating at application startup, so without this the
// schema is never created and every service and host fails on its first query.
// One per database, because one database per service or host (§7.1, ADR-051).
var orderingMigrator = builder
    .AddProject<Projects.Ordering_Migrator>("ordering-migrator")
    .WithReference(orderingDb, connectionName: "OrderingMigrator")
    .WaitFor(sql);

var catalogMigrator = builder
    .AddProject<Projects.Catalog_Migrator>("catalog-migrator")
    .WithReference(catalogDb, connectionName: "CatalogMigrator")
    .WaitFor(sql);

var bffMigrator = builder
    .AddProject<Projects.Web_Bff_Migrator>("bff-migrator")
    .WithReference(bffDb, connectionName: "BffMigrator")
    .WaitFor(sql);

var ordering = WithPlatformIdentity(
    builder
        .AddProject<Projects.Ordering_Api>("ordering-api")
        .WithReference(orderingDb)
        .WaitFor(orderingDb)
        .WithReference(cache)          // → ConnectionStrings:RedisCache
        .WithReference(coordination)   // → ConnectionStrings:RedisCoordination
        .WithReference(mq)
        .WaitFor(mq)
        // Gate on the migrator completing, not merely starting — the Compose
        // equivalent is service_completed_successfully (§14.1).
        .WaitForCompletion(orderingMigrator)
        // Pinned ports, moved as Catalog's are below; 8082 because Catalog's
        // h2c listener holds 8081 (ADR-052).
        .WithEnvironment("Kestrel__Endpoints__Rest__Url", "http://localhost:5101")
        .WithEnvironment("Kestrel__Endpoints__Grpc__Url", "http://localhost:8082")
        .WithHttpHealthCheck("/health/ready"));   // authority only — no peer calls

// Catalog and Ordering are the resources here whose ports Aspire does NOT get
// to choose. Each appsettings.json declares Kestrel:Endpoints — 8080 for REST
// and 8081 for its gRPC surface, because a cleartext port cannot serve HTTP/1.1
// and h2c at once — and declaring that section suppresses ASPNETCORE_URLS and
// ASPNETCORE_HTTP_PORTS entirely, which is how Aspire assigns a port. So each
// binds 8080 whatever this host allocates, and 8080 is what AddKeycloak took
// above: they fight for it unless each is moved.
//
// Move Catalog's, with the same configuration keys the compose README uses for
// `dotnet run` — a higher provider is the only thing that overrides that file:
//
//     .WithEnvironment("Kestrel__Endpoints__Rest__Url", "http://localhost:5102")
//     .WithEnvironment("Kestrel__Endpoints__Grpc__Url", "http://localhost:8081")
//
// And note what WithReference cannot do here: the BFF's hop address is the
// literal http://catalog-api:8081 (§9.7, §15.4), so a reference does not
// re-point it. Aspire runs the BFF against whatever answers on that name.
//
// No seed runs under this sample, whose migrators set no Seed__Enabled: §14.3
// delivers seeded events only through §14.1's start order, and Catalog here
// waits on neither Ordering nor the BFF, so a seeded event would go unheard.
var catalog = WithPlatformIdentity(
    builder
        .AddProject<Projects.Catalog_Api>("catalog-api")
        .WithReference(catalogDb)
        .WaitFor(catalogDb)
        .WithReference(cache)
        .WithReference(coordination)
        .WithReference(mq)
        .WaitForCompletion(catalogMigrator)
        .WithEnvironment("Kestrel__Endpoints__Rest__Url", "http://localhost:5102")
        .WithEnvironment("Kestrel__Endpoints__Grpc__Url", "http://localhost:8081")
        .WithHttpHealthCheck("/health/ready"));

// The gateway validates JWTs too (§11.2) — it is the component most visible
// when the authority is missing. No callerClientId: YARP forwards the caller's
// token rather than minting one of its own, so there is no "gateway" Keycloak
// client and no gateway secret.
WithPlatformIdentity(
    builder
        .AddProject<Projects.Gateway_Api>("gateway")
        .WithReference(ordering)
        .WithReference(catalog)
        // The same edge shape Compose declares (§14.1), for the same reason:
        // locally the gateway IS the edge, and browsers reach it directly.
        // Diverging here would make a SPA work under one local path and fail
        // under the other.
        .WithEnvironment("Ingress__Enabled", "false")
        .WithEnvironment("Cors__Enabled", "true")
        .WithEnvironment("Cors__Origins__0", "http://localhost:5173")
        // /health/ready, like every other resource and like the chart in
        // §15.3 — an empty readiness set is still the right question here,
        // and probing liveness instead would make the gateway the one
        // component whose local gate differs from its deployed one. The
        // emptiness is a declaration rather than a silence: the host passes
        // ownsNoReadinessDependencies to MapCommonHealthEndpoints (§13.5),
        // which is an EXEMPTION and not a check. A gateway that later acquired
        // a readiness dependency would still start with an empty set until
        // somebody removed that argument — the guard refuses a host that
        // forgot its checks, not one that declared it has none.
        .WithHttpHealthCheck("/health/ready")
        .WithExternalHttpEndpoints());

// The only resource with a callerClientId, and the only one this model needs:
// a host that calls a peer holds client credentials (§9.7, §11.5), §15.4 says
// which other hosts do (ADR-052), and this sample runs none of them. For a host
// added that calls a peer, ADR-017's hop budget is the first check.
WithPlatformIdentity(
    builder
        .AddProject<Projects.Web_Bff>("web-bff")
        .WithReference(bffDb)
        .WaitFor(bffDb)
        .WithReference(mq)
        .WaitFor(mq)
        .WaitForCompletion(bffMigrator)
        .WithReference(catalog)
        .WithHttpHealthCheck("/health/ready"),
    callerClientId: "web-bff");

builder.Build().Run();
```

```bash
aspire run
```

Matching the names is what makes the two local paths interchangeable: §14.1's
Compose sets `ConnectionStrings__RedisCache` by hand, Aspire derives the same
key from the resource name, and the service reads one key either way. A
mismatch here breaks only the Aspire path — which nothing in CI exercises, so it
would surface as "Aspire doesn't work on my machine" rather than as a defect.

Two deliberate local simplifications, both matching what the Compose and test
environments already do:

- **`OrderingMigrator` points at the same SQL login as the runtime connection.**
  [§7.1](07-persistence.md)'s two identities are a production control; locally there is one `sa`
  account, exactly as [§12.4](12-test-strategy.md)'s fixture notes. The *key* still differs, so the
  migrator reads the name it will read in production.
- **The client secret is an Aspire parameter**, prompted once and stored in user
  secrets, rather than a value in the AppHost. It is the same obligation [§15.4](15-cicd-deployment.md)
  records — a required setting needs a source in every environment — met a third
  way.

> **The AppHost is a deployment environment, and drifts like one.** It is the
> only one with no automated exercise: Compose runs in CI, Helm is rendered and
> asserted in CI (§15.1) as well as applied by CD, and the integration fixture
> builds its own. Every configuration change
> lands in three places and can be forgotten in the fourth without anything
> failing. When a required key is added (§15.4), this file is the one to check
> last and the one most likely to be wrong.

**The escape hatch.** Adding one line emits a Compose file from the same model,
so the Aspire dependency is reversible:

```csharp
builder.AddDockerComposeEnvironment("compose");
```

```bash
aspire publish   # writes docker-compose.yaml to ./aspire-output
```

**What adopting Aspire costs.** The AppHost becomes the source of truth for
topology, so the team must learn its model. The API surface has moved quickly —
four major versions in roughly fifteen months. And it is a visible Microsoft
tooling opinion in an otherwise portable stack.

**What removing it costs.** Aspire is not in the production request path;
deployed containers are plain ASP.NET Core. The coupling is four things: the
AppHost project (delete it), `Common.Web` (your own code — keep it, swapping
only service discovery for DNS), the `Aspire.*` client integration packages (one
line per resource per service reverts to standard registration), and the
connection-string environment variable conventions (reproduce them in Compose).
For a platform this size, roughly one to three days of mechanical work.

> **Decision** — Compose is the documented baseline; Aspire is offered as an
> optional accelerator. See [ADR-011](adr/ADR-011-compose-baseline-aspire-optional.md).

## 14.3 Seed data

Seeding runs from the migrator container, is idempotent, and is
development-only — and the last of those is a gate rather than an
instruction, so most of this section is that gate. It should produce
enough data to exercise pagination and caching: a catalogue of three products
hides every performance problem you have.

**`CatalogSeeder` and `InventorySeeder` write what their services' own
commands would have.** Each product goes in with the `ProductPublished`
outbox row `PublishProduct` stages, and each count with the
`StockLevelChanged` row `SetOnHand` stages, so Ordering's prices, the BFF's
names and Catalog's stock levels fill through the platform's own delivery
([§9.4](09-messaging.md)) and the seeded state is one the system could have
reached. §14.1's start order is what makes that delivery arrive: a fanout
publish with no queue bound to it is dropped, so `catalog-api` waits for its
consumers to be ready and `inventory-api` waits for Catalog. Inventory is
seeded directly rather than from Catalog's event, because stock reaches it by
the admin path ([§10.2](10-api-gateway.md)'s
`inventory-admin` route) and from no event
([§3.2](03-bounded-contexts.md)). Both write SQL through the migration's own
`DbContext`, since [§4.2](04-solution-structure.md) keeps the migrator off
Application and Domain; each skips an id that already has a row, so a second
run changes nothing; and the ids are stable, owned by
[`deploy/compose/README.md`](../../deploy/compose/README.md), which
publishes them.

**The gate exists because of where they run.** The migrator container is the
one artefact guaranteed to run in production —
[§15.2](15-cicd-deployment.md) builds a migrator image beside every API, and
[§7.4](07-persistence.md)'s Job runs it as Helm's `pre-install,pre-upgrade`
hook on every release, holding the DDL identity §7.1 gives it and nothing
else in the platform holds. A seeder there with no gate writes demo rows into
a production database on the first `helm upgrade` — idempotently, so nothing
fails, no hook goes red, and the deploy log says only that a migration ran.

**The gate is two conditions at the job host's composition root, and both fail
closed.** `MigratorHost.Build` is where they go, because that is where this
solution already decides on an environment: `Common.Web`'s
`AuthenticationExtensions` reads `builder.Environment.IsDevelopment()` twice,
once to refuse a plain-HTTP authority at startup and once to set
`RequireHttpsMetadata` ([§11.3](11-identity-authorization.md)). Registration is
the effect rather than a branch inside the runner — a migrator that must not
seed has no seeder in its container to resolve, so there is exactly one place
configuration is read.

In `src/Services/Catalog/Catalog.Migrator/MigratorHost.cs`, beside the
`DbContext` registration:

```csharp
bool requested = bool.TryParse(builder.Configuration["Seed:Enabled"], out bool enabled) && enabled;

if (requested && builder.Environment.IsDevelopment())
    builder.Services.AddScoped<CatalogSeeder>();
```

The flag is read as a string and parsed, never with
`Configuration.GetValue<bool>`. A variable set to the empty string arrives as
`""` rather than null, and `GetValue<bool>` throws `InvalidOperationException`
on it, which turns a stray key into a failed pre-upgrade hook and a blocked
release. `TryParse` answers the same way for `""`, for null, for `"1"` and for
`"yes"`: do not seed.

`MigrationRunner`, beside it in `MigrationRunner.cs`, takes the seeder as a
primary-constructor parameter with a **default value**, and the `= null` is
the whole of what makes it optional — the trap below says why:

```csharp
public sealed class MigrationRunner(
    CatalogDbContext db,
    ILogger<MigrationRunner> logger,
    CatalogSeeder? seeder = null)
```

The runner is otherwise §7.4's: `MigrateAsync`, a log line and an exit code.
After `MigrateAsync` returns, it seeds when it has a seeder and logs
`NotSeeding` when it has none. The log line is not decoration: a gate that
fails closed in silence is indistinguishable from a seeder that is broken, and
it is the seeder a developer debugs.

> **Trap — a nullable reference type is not an optional dependency.**
> `CatalogSeeder?` is an annotation the compiler reads and
> `Microsoft.Extensions.DependencyInjection` never does. A constructor parameter
> whose service is unregistered and which carries **no default value** makes
> resolving `MigrationRunner` throw `InvalidOperationException` — *unable to
> resolve service for type `CatalogSeeder` while attempting to activate
> `MigrationRunner`* — so the migrator that must not seed becomes the migrator
> that cannot start, and the gate fails the pre-upgrade hook *before* the
> migration it was guarding. That is the opposite of failing closed: the
> conditional registration above is only safe because the container falls back
> to a parameter's default value when it cannot resolve one, which is what
> `= null` supplies. Resolving it explicitly with
> `IServiceProvider.GetService<CatalogSeeder>()` is the other correct spelling
> and is not the one taken here, because it puts a service locator inside the
> one type §7.4 keeps to `Database.Migrate()` and an exit code.

**Both conditions are load-bearing, and they close different doors.** The flag
is the half somebody writes down, and [§15.3](15-cicd-deployment.md) says what
a chart would have to grow before one could reach a Job container at all. The
environment name is the half no values file in this repository can supply: no
chart sets an environment name for any workload, so every deployed migrator
runs as `Production` and `IsDevelopment()` is false there whatever flag an
overlay carries. `!IsProduction()` is not the same test, and the difference is
measured rather than stylistic — with the variable set to the empty string
`EnvironmentName` is `""`, `IsDevelopment()` is false and `IsProduction()` is
false too, so the negation admits precisely the blank value a templating
mistake produces.

> **Trap — `ASPNETCORE_ENVIRONMENT` does not reach the migrator.** The job host
> is `Host.CreateApplicationBuilder` (§7.4) rather than
> `WebApplication.CreateBuilder`, and the generic host binds its environment
> from `DOTNET_ENVIRONMENT`. Measured on .NET 10: with
> `ASPNETCORE_ENVIRONMENT=Development` set and `DOTNET_ENVIRONMENT` unset,
> `EnvironmentName` is `Production` and `IsDevelopment()` returns false. So the
> obvious guard is one that never opens, and what a developer then debugs is
> the seeder — the gate is silent and the seeder is the visible half. So
> §14.1's `catalog-migrator` and `inventory-migrator` set
> `DOTNET_ENVIRONMENT: Development` beside `Seed__Enabled` and their
> connection strings: the local half of the same switch, carried by the unit
> that runs the seeder.

**Turning seeding on has to be a diff, and a values file alone cannot make
it.** The shared migration-Job template renders exactly one `env` entry — the
migrator connection string of §7.1 — so `Seed__Enabled` has no route into that
container. Enabling it in a cluster would mean editing the library chart's
template *and* a values file — two lines in a reviewed pull request, rather
than a variable somebody exports onto a namespace. §15.3 carries the chart
half of that rule.

**A seeder creates rows and not principals.** §14.1's realm is imported by
Keycloak itself, from `deploy/compose/keycloak/realm-export.json` under
`start-dev --import-realm` — a container and a mode that exist in Compose and
nowhere else. Nothing in a migrator creates a subject, and a demo principal in
a production identity store is an account with a published password. Seed the
databases the gate above protects, and leave identity to an import no deployed
artefact reaches.

---

[← §13 Observability](13-observability.md) · [Index](README.md) · [§15 CI/CD →](15-cicd-deployment.md)
