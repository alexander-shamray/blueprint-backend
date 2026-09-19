# 14. Local development

## 14.1 Docker Compose — the baseline

One command starts the platform. This is the documented default; it requires
only Docker and works identically on every operating system and in CI.

The Compose file is an index and one file per deployable unit, so a service's
environment is a file that service's PR owns rather than a block in a file
every service PR edits — [`docs/change-locality.md`](../change-locality.md)'s
rule applied to the one deployment artefact every service has to touch:

```yaml
# deploy/compose/docker-compose.yml
name: commerce

include:
  - infrastructure.yml
  - services/catalog.yml
  - services/gateway.yml
  - services/ordering.yml
  - services/web-bff.yml
  - services/inventory.yml
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

What the rest of this section specifies is the model those files make between
them, which is what Compose reads. It is written here as one document, in
reading order and with the elisions this chapter has always made, rather than
as any one file's contents:

```yaml
# the model: deploy/compose/docker-compose.yml, infrastructure.yml
# and services/*.yml, as one document
name: commerce

services:
  sql:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}"
      MSSQL_PID: Developer
    # Every mapping in the model publishes on 127.0.0.1 rather than on every
    # interface — infrastructure.yml's and every unit's alike — argued once
    # here rather than per service or per file: the credentials
    # are development defaults, so the interface is what stands in front of
    # them. The callout below the endpoint table carries the argument.
    ports: [ "127.0.0.1:1433:1433" ]
    volumes: [ sql-data:/var/opt/mssql ]
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -C -Q 'SELECT 1'"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 30s

  # Two Redis instances, because eviction policy cannot be shared — §8.1.
  redis-cache:
    image: redis:7-alpine
    command: redis-server --maxmemory 256mb --maxmemory-policy allkeys-lru
    ports: [ "127.0.0.1:6379:6379" ]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]

  redis-coordination:
    image: redis:7-alpine
    # noeviction: locks and idempotency keys must never be evicted. The
    # {service}:denylist: namespace §8.1 reserves beside them has no writer
    # (ADR-033). Appendonly so a restart does not silently release held locks.
    command: redis-server --appendonly yes --maxmemory 128mb --maxmemory-policy noeviction
    ports: [ "127.0.0.1:6380:6379" ]
    volumes: [ redis-coordination-data:/data ]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      retries: 5

  # Built rather than pulled, and the ONE infrastructure service that is.
  # rabbitmq/Dockerfile adds the delayed message exchange plugin §9.6's saga
  # timeouts are scheduled through (ADR-021), pinned by digest; the base
  # image is still an official tag, one line inside it.
  rabbitmq:
    build:
      context: rabbitmq
    ports: [ "127.0.0.1:5672:5672", "127.0.0.1:15672:15672" ]
    volumes: [ rabbit-data:/var/lib/rabbitmq ]
    healthcheck:
      # check_running answers "is the broker up", which was the whole question
      # while the image was stock. It is not any more: a broker missing the
      # plugin is running and healthy while every saga schedule hangs on a
      # declare it refuses (ADR-021).
      #
      # is_enabled, NOT `list -e … | grep`: that spelling matches the pattern
      # echoed in the command's own banner and passes on a stock broker —
      # measured. The exit status is the only part that reports the answer.
      test: ["CMD-SHELL", "rabbitmq-diagnostics check_running && rabbitmq-plugins is_enabled rabbitmq_delayed_message_exchange"]
      interval: 10s
      retries: 5

  keycloak:
    image: quay.io/keycloak/keycloak:26.0
    command: start-dev --import-realm
    environment:
      KC_BOOTSTRAP_ADMIN_USERNAME: admin
      KC_BOOTSTRAP_ADMIN_PASSWORD: admin
      # One issuer, whichever host asks. Without it Keycloak derives the issuer
      # from each request's Host header, so a token minted through
      # localhost:8080 and a discovery document read through keycloak:8080
      # disagree — and ValidateIssuer (§11.3) rejects every token obtained the
      # way this chapter documents. The second variable puts the backchannel
      # back on the container route, which is the half the services need.
      KC_HOSTNAME: http://localhost:8080
      KC_HOSTNAME_BACKCHANNEL_DYNAMIC: "true"
      KC_HEALTH_ENABLED: "true"
    ports: [ "127.0.0.1:8080:8080" ]
    volumes: [ ./keycloak/realm-export.json:/opt/keycloak/data/import/realm.json:ro ]
    # The image has a shell but no HTTP client, so this is a bash TCP
    # redirection against the management port — the form Keycloak's own
    # documentation gives. Without a healthcheck `up --wait` returns when the
    # process launches rather than when the realm is importable, and the first
    # token request races the import.
    healthcheck:
      test:
        [
          "CMD-SHELL",
          "exec 3<>/dev/tcp/localhost/9000; echo -e 'GET /health/ready HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3; cat <&3 | grep -q '\"status\": \"UP\"'"
        ]
      interval: 5s
      timeout: 5s
      retries: 30
      start_period: 20s

  otel-collector:
    image: otel/opentelemetry-collector-contrib:latest
    command: [ "--config=/etc/otel/config.yaml" ]
    volumes: [ ./otel/config.yaml:/etc/otel/config.yaml:ro ]
    ports: [ "127.0.0.1:4317:4317", "127.0.0.1:4318:4318" ]

  grafana:
    image: grafana/otel-lgtm:latest
    ports: [ "127.0.0.1:3000:3000" ]

  # ---- Application services ----

  ordering-migrator:
    build:
      context: ../../..
      dockerfile: src/Services/Ordering/Ordering.Migrator/Dockerfile
    environment:
      # Migrator identity (DDL) — §7.1. Locally both keys resolve to the one
      # sa login (§14.2's stated simplification); in production they are
      # separate secrets on separate workloads. The default is inline, nesting
      # the password's own default, because .env.example promises every
      # variable a working default — bare ${…} interpolates to an empty
      # string on a clean checkout, which config -q accepts and the first
      # query does not.
      ConnectionStrings__OrderingMigrator: "${ORDERING_MIGRATOR_CONNECTION:-Server=sql;Database=Ordering;User Id=sa;Password=${SQL_PASSWORD:-Local_Dev_Pa55w0rd!};TrustServerCertificate=True}"
    depends_on:
      sql: { condition: service_healthy }
    restart: "no"

  ordering-api:
    build:
      context: ../../..
      dockerfile: src/Services/Ordering/Ordering.Api/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      # Runtime identity (DML only) — never the migrator connection.
      ConnectionStrings__Ordering: "${ORDERING_CONNECTION:-Server=sql;Database=Ordering;User Id=sa;Password=${SQL_PASSWORD:-Local_Dev_Pa55w0rd!};TrustServerCertificate=True}"
      ConnectionStrings__RabbitMq: "amqp://ordering-svc:local-dev-ordering@rabbitmq:5672"
      # The authority, to validate inbound tokens (§11.2). No Identity__Client__*:
      # Ordering calls no peer synchronously — prices come from a local
      # projection (§6.4) and the rest goes over the broker. Only the BFF holds
      # client credentials (§9.7, §11.5).
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
      # §8.1's two connections, joined on the rule that brought every line
      # above: an environment variable nothing reads is the container form of
      # an unused registration, so each waits for the PR whose code first reads
      # it. §8.5's IdempotencyBehavior is that PR — it claims a
      # {service}:idem: key before any protected command runs. §8.4's cache
      # invalidation was the expected trigger and was not the one that came.
      #
      # BOTH, though only the coordination one is read: AddRedisConnections is
      # a single call by design (§8.2) and reads both eagerly, so a host given
      # one key throws naming the other.
      #
      # 6379 on both — the host-side ports differ (6379/6380), the
      # container-side ports do not.
      ConnectionStrings__RedisCache: "redis-cache:6379"
      ConnectionStrings__RedisCoordination: "redis-coordination:6379"
    ports: [ "127.0.0.1:5101:8080" ]
    depends_on:
      ordering-migrator: { condition: service_completed_successfully }
      rabbitmq:          { condition: service_healthy }
      # service_healthy, not service_started: AbortOnConnectFail is false
      # (§8.1's "degrade, don't die"), so the host would start against a Redis
      # still booting and the first protected command would fail on a claim
      # instead — a symptom nothing connects to a container that was not ready.
      redis-cache:        { condition: service_healthy }
      redis-coordination: { condition: service_healthy }
      # service_healthy rather than service_started, though the API does not
      # need Keycloak to boot — JwtBearer fetches the discovery document
      # lazily. `up --wait` gates on every service's health, and the value is
      # for whoever types the README's token command the moment it returns:
      # under service_started that command races the realm import.
      keycloak:          { condition: service_healthy }

  # catalog-api, inventory-api, payments-api, shipping-worker and
  # notifications-worker follow the same shape — and "the same shape" is a
  # PAIR: a {service}-migrator one-shot plus the service itself gated on
  # `condition: service_completed_successfully`. Every service owns a database
  # and none may migrate at startup (§4.1, ADR-007), so a service added here
  # without its migrator starts against an empty schema.

  gateway:
    build:
      context: ../../..
      dockerfile: src/Gateway/Gateway.Api/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      # The authority, because the gateway validates JWTs like every other host
      # (§11.2). NOT Identity__Client__* — those are for calling other services
      # (§11.5), and the gateway calls nobody: YARP forwards the caller's token.
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      # Locally the gateway IS the edge: nothing forwards, RemoteIpAddress is
      # already the client, and trusting X-Forwarded-For would let any caller
      # pick its own rate-limit bucket. In Kubernetes this is true (§15.3).
      Ingress__Enabled: "false"
      # Browsers hit the gateway directly in dev. The SPA's dev origin is
      # 5173 (Vite's default): 3000 belongs to Grafana, in infrastructure.yml.
      Cors__Enabled: "true"
      Cors__Origins__0: "http://localhost:5173"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports: [ "127.0.0.1:5000:8080" ]
    depends_on:
      keycloak: { condition: service_healthy }
      # Every destination that exists, and only those — a Compose dependency
      # on a service Compose cannot see fails the whole `up`, where a route to
      # one costs nothing at startup. So a name joins this list with the PR
      # that builds its service.
      catalog-api: { condition: service_started }
      inventory-api: { condition: service_started }
      ordering-api: { condition: service_started }
      web-bff: { condition: service_started }

  # The one host with client credentials, because it is the one host that calls
  # a peer synchronously (§9.7). Everything else here has the authority only.
  # Named web-bff, matching the Aspire resource (§14.2) and the YARP
  # destination (§10.2) — the gateway resolves the destination by hostname, so
  # the container name IS the routing configuration.
  web-bff:
    build:
      context: ../../..
      dockerfile: src/BFF/Web.Bff/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      # Required by ValidateOnStart (§15.4) — this host refuses to boot
      # without them. Local values only; production mounts a secret.
      Identity__Client__ClientId: "web-bff"
      Identity__Client__ClientSecret: "${BFF_CLIENT_SECRET:-local-dev-secret}"
      Identity__Client__Scope: "commerce-api"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports: [ "127.0.0.1:5200:8080" ]
    depends_on:
      # Keycloak only. catalog-api is elided from this file (see the comment
      # above the gateway), and Compose rejects a dependency on a service it
      # cannot see — one undefined name fails the whole `up`, not one service.
      keycloak: { condition: service_healthy }

volumes:
  sql-data:
  redis-coordination-data:
  rabbit-data:
```

The model is delivered in [Appendix C](appendix-c-delivery-plan.md)'s order
rather than at once. PR-06 ships the seven infrastructure services above;
each application block lands with the PR that builds its image — the
scaffold of [§4.5](04-solution-structure.md) writes the service's own unit
file and the one line that includes it, along with both `infra-only`
exclusions below, its `.env.example` variables and its row in
`deploy/compose/README.md`. The
`docker-compose.infra-only.yml` override below arrives with the first
containerised service, there being nothing to exclude before it.

The realm file shipped as a placeholder — realm name and `enabled` only —
from PR-06 until PR-16 replaced it with a full Keycloak export: the
`commerce-api` client scope with its audience and permission mappers, the
`commerce-api` client holding the permission vocabulary as client roles, a
browser client, and two development logins. **A full export rather than a
readable summary, and [§11.5](11-identity-authorization.md) argues why** —
Keycloak treats a `clientScopes` array as the complete set, so a trimmed file
silently drops the built-in scopes and with them `sub`.

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
```

| Endpoint | URL |
|---|---|
| Gateway | http://localhost:5000 |
| Keycloak | http://localhost:8080 (admin/admin) |
| RabbitMQ management | http://localhost:15672 — **no login ships**; see below |
| Grafana | http://localhost:3000 |

> **Every published port binds `127.0.0.1`, not `0.0.0.0`.** The credentials
> in the table above are development defaults on purpose, which makes the
> interface the control standing in front of them — Compose's short syntax
> with no host-IP prefix publishes on every interface, so `docker compose up`
> on a café or office network offers `sa`, two passwordless Redis instances,
> two service accounts and Keycloak's admin console to every peer on it.
> Every URL in the table is already a `localhost` one, so the prefix takes
> nothing away from the workflow this chapter documents.

The realm's own logins are `demo/demo`, which holds every permission a shipped
endpoint requires — `deploy/compose/README.md` carries the current list rather
than this sentence, because a subset named here goes stale with each service
and did exactly that when Ordering landed — and `browser/browser`, which holds
nothing and is the account a refusal is proved against. `orders:admin` is
grantable and held by neither, deliberately: it overrides §11.4's ownership
check, which stays demonstrable only while no shipped login can bypass it. Both are development defaults on the same terms as the three above —
the deliberate local-development exception to §11.6, which every deployed
environment replaces with real users out of a directory.

`deploy/compose/README.md` is the keyboard inventory of what runs today —
every port and credential of the seven infrastructure services, beside the
file it describes. The table above is the finished platform's surface, and its
Gateway row arrived with the gateway's image
([Appendix C](appendix-c-delivery-plan.md), PR-17); the Keycloak, RabbitMQ and
Grafana rows have been true since PR-06.

The gateway's own block takes no `depends_on` on a service it routes to except
the ones that exist — Compose rejects a dependency it cannot see, and one
undefined name fails the whole `up` rather than one service. Its *routes* are
under no such constraint and [§10.2](10-api-gateway.md) ships all four, so
a path answers 502 until its service lands. A route is configuration the
gateway reads; a `depends_on` is a name Compose has to resolve. Which
destinations Compose can see is the `include` list in
`deploy/compose/docker-compose.yml`, the owner of that fact.

**The fence above and the shipped file gate on the same services**: the
`depends_on` list in `deploy/compose/services/gateway.yml` is the owner of
which, and the fence is its sample. Read the fence for the shape of a block;
a destination still joins the dependency list with the change that builds
it, so the two would diverge again at the next service.

**The BFF's own block gained a `depends_on` the fence above does not show**, on
`catalog-api`, and the asymmetry with the gateway is the point: the gateway
routes to services that may not exist, where the BFF *calls* one that does
(§9.7). Its hop is over `catalog-api:8081` — a second, HTTP/2-only Kestrel
endpoint, because a cleartext port cannot serve HTTP/1.1 and h2c at once — and
that port is published to no host and reached by no route.

The collector's mounted configuration is the smallest correct pipeline —
OTLP in on both protocols, a batch processor, OTLP out to the LGTM
container, which ingests OTLP directly:

```yaml
# deploy/compose/otel/config.yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  batch:

exporters:
  otlphttp:
    endpoint: http://grafana:4318

service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlphttp]
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlphttp]
    logs:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlphttp]
```

An override file runs infrastructure in containers while services run on the
host with a debugger attached — the usual inner-loop compromise:

```bash
# Both from the repository root — the two commands share one working
# directory, or the pair cannot be pasted as written.
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.infra-only.yml up -d
dotnet run --project src/Services/Ordering/Ordering.Api
```

**A host process reads none of the container `environment:` blocks, so every
key a service throws without has to be supplied to it.** **Five** do that
today, each named by the registration that reads it —
`ConnectionStrings__<Service>` (§7.1's runtime key, `AddSqlServer`),
`ConnectionStrings__RabbitMq` (§9's bus), `Identity__Authority`
([§11.3](11-identity-authorization.md), read eagerly by
`AddJwtAuthentication`), and the two Redis connections
([§8.1](08-caching-redis.md), read eagerly by `AddRedisConnections`). The
values differ from the container ones only in the host name and, for the
second Redis instance, the port — because the compose file publishes each one:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Ordering='Server=localhost;Database=Ordering;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://ordering-svc:local-dev-ordering@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
export ConnectionStrings__RedisCache='localhost:6379'
export ConnectionStrings__RedisCoordination='localhost:6380'
```

> **The two Redis ports differ here and are identical in the compose blocks,
> and getting that backwards is a connection to the wrong instance rather than
> an error.** Both containers listen on Redis's own 6379, so inside the network
> the addresses differ only by host name; the published ports are 6379 and
> **6380**, so on the host they differ only by port. A block copied from the
> compose file would point both connections at the cache — the same defect the
> chart's key guard refuses at render time (§15.3), arriving where nothing
> checks it.
>
> **This block said three keys until §8.5's PR gave `AddRedisConnections` its
> first callers.** It reads both connection strings eagerly and throws naming
> the missing one, so the `dotnet run` above exited before startup for anyone
> following the instructions — the failure §14.1's own rule predicts, in the
> half of the chapter that rule does not cover.

**The key is the service's own**, and the block above is `Ordering.Api`'s
because that is the host the fence names. `AddOrderingInfrastructure` reads
`ConnectionStrings:Ordering` and `AddSqlServer` throws without it, so
exporting Catalog's key here — which this sample did until PR-18 — is a set of
instructions that cannot start the process it precedes. Running the migrator
instead takes `ConnectionStrings__OrderingMigrator`, which is §7.1's whole
point: two keys, one of which may issue DDL.

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

The override's whole content is a profile per application service. An override
cannot delete a service, but profiles gate activation and nothing activates
this one, so the default `up` skips every service it names — declaratively,
with no duplicated configuration to drift:

```yaml
# deploy/compose/docker-compose.infra-only.yml
services:
  ordering-migrator:
    profiles: [ "excluded" ]
  ordering-api:
    profiles: [ "excluded" ]
  # ... every unit the index includes joins this list in the same PR that
  # adds it; an omitted one silently keeps starting. This is the one Compose
  # file the per-unit split leaves shared, because an override merges over a
  # resolved model and cannot be divided the way the model is. The §4.5
  # scaffold writes both halves, which is the reliable way to keep a rule
  # whose only symptom is a container nobody asked for.
```

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

// The stock image, and since ADR-021 that is a STATED GAP rather than a
// parity with §14.1. Ordering's saga schedules through the delayed message
// exchange, which is a community plugin no official image carries — so this
// line brings up a broker that takes UseDelayedMessageScheduler, connects,
// reports healthy, and then hangs on the first saga schedule. §14.1 builds
// deploy/compose/rabbitmq for exactly that reason.
//
// It is left as the stock call because Aspire is not adopted (ADR-011) and a
// sample nobody compiles is the wrong place to invent an image-build API.
// **Adopting Aspire means closing this first**: point the resource at the
// same Dockerfile, or run Ordering against Compose. Catalog's test fixture
// may stay on the base tag and says why — it schedules nothing — and this
// AppHost runs Ordering, so it does not have that excuse.
var mq = builder.AddRabbitMQ("RabbitMq").WithManagementPlugin();

// One database per service that this AppHost runs. The rest are omitted
// deliberately — adding a database without the service and migrator
// resources that own it creates a schema nothing maintains, which is the
// shape §4.1 rules out.
var orderingDb = sql.AddDatabase("Ordering");
var catalogDb = sql.AddDatabase("Catalog");

var keycloak = builder
    .AddKeycloak("keycloak", 8080)
    .WithRealmImport("./keycloak/realm-export.json");

// ReferenceExpression, not string concatenation: GetEndpoint() returns a
// deferred reference — the port is not allocated yet. Concatenating it with +
// would stringify the object and write a placeholder into the environment.
var authority = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/commerce");

// Every host validates JWTs (§11.2), so every host needs the authority.
// Applied by one helper rather than repeated per resource — the previous
// version configured ordering-api and left catalog and the gateway with a null
// authority, which fails only at first request.
//
// Client credentials are a SEPARATE concern with a narrower audience: only a
// host that calls another service (§11.5) presents them. Passing a clientId to
// a host that makes no outbound call provisions a Keycloak client, prompts for
// a secret and mounts it, all for credentials nothing ever sends.
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
// schema is never created and every service fails on its first query.
// One per service, because one database per service (§7.1).
var orderingMigrator = builder
    .AddProject<Projects.Ordering_Migrator>("ordering-migrator")
    .WithReference(orderingDb, connectionName: "OrderingMigrator")
    .WaitFor(sql);

var catalogMigrator = builder
    .AddProject<Projects.Catalog_Migrator>("catalog-migrator")
    .WithReference(catalogDb, connectionName: "CatalogMigrator")
    .WaitFor(sql);

var ordering = WithPlatformIdentity(
    builder.AddProject<Projects.Ordering_Api>("ordering-api")
        .WithReference(orderingDb).WaitFor(orderingDb)
        .WithReference(cache)          // → ConnectionStrings:RedisCache
        .WithReference(coordination)   // → ConnectionStrings:RedisCoordination
        .WithReference(mq).WaitFor(mq)
        // Gate on the migrator completing, not merely starting — the Compose
        // equivalent is service_completed_successfully (§14.1).
        .WaitForCompletion(orderingMigrator)
        .WithHttpHealthCheck("/health/ready"));   // authority only — no peer calls

// Catalog is the one resource here whose ports Aspire does NOT get to choose.
// Its appsettings.json declares Kestrel:Endpoints — 8080 for REST and 8081 for
// §9.7's gRPC hop, because a cleartext port cannot serve HTTP/1.1 and h2c at
// once — and declaring that section suppresses ASPNETCORE_URLS and
// ASPNETCORE_HTTP_PORTS entirely, which is how Aspire assigns a port. So it
// binds 8080 whatever this host allocates, and 8080 is what AddKeycloak took
// above: the two fight for it unless one is moved.
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
var catalog = WithPlatformIdentity(
    builder.AddProject<Projects.Catalog_Api>("catalog-api")
        .WithReference(catalogDb).WaitFor(catalogDb)
        .WithReference(cache)
        .WithReference(coordination)
        .WithReference(mq)
        .WaitForCompletion(catalogMigrator)
        .WithEnvironment("Kestrel__Endpoints__Rest__Url", "http://localhost:5102")
        .WithEnvironment("Kestrel__Endpoints__Grpc__Url", "http://localhost:8081")
        .WithHttpHealthCheck("/health/ready"));

// The gateway validates JWTs too (§11.2) — it is the component most visible
// when the authority is missing, and was the one previously left without it.
// No callerClientId: YARP forwards the caller's token rather than minting one
// of its own, so there is no "gateway" Keycloak client and no gateway secret.
WithPlatformIdentity(
    builder.AddProject<Projects.Gateway_Api>("gateway")
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

// The only resource with a callerClientId, matching Compose (§14.1): the BFF
// is the only host that calls a peer synchronously (§9.7). If a second one
// ever appears, ADR-017's hop budget is the thing to check first.
WithPlatformIdentity(
    builder.AddProject<Projects.Web_Bff>("web-bff")
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
instruction, so the rest of this section is that gate. It should produce
enough data to exercise pagination and caching: a catalogue of three products
hides every performance problem you have.

**Nothing seeds today, and this section is a specification rather than a
description.** No `*.Migrator` project holds a seeder, and no chart or Compose
file carries a flag for one. That is why the mechanism is written down now
rather than after: the container the sentence above names is the one artefact
guaranteed to run in production — [§15.2](15-cicd-deployment.md) builds a
migrator image beside every API, and [§7.4](07-persistence.md)'s Job runs it as
Helm's `pre-install,pre-upgrade` hook on every release, holding the DDL
identity §7.1 gives it and nothing else in the platform holds. A seeder
written where the paragraph above tells an implementer to put it, with no
gate, writes demo rows into a production database on the first
`helm upgrade` — idempotently, so nothing fails, no hook goes red, and the
deploy log says only that a migration ran.

**The gate is two conditions at the job host's composition root, and both fail
closed.** `MigratorHost.Build` is where they go, because that is where this
solution already decides on an environment: `Common.Web`'s
`AuthenticationExtensions` reads `builder.Environment.IsDevelopment()` twice,
once to refuse a plain-HTTP authority at startup and once to set
`RequireHttpsMetadata` ([§11.3](11-identity-authorization.md)). Registration is
the effect rather than a branch inside the runner — a migrator that must not
seed has no seeder in its container to resolve, so there is exactly one place
configuration is read.

```csharp
// src/Services/Ordering/Ordering.Migrator/MigratorHost.cs, beside the
// DbContext registration.
//
// Read as a string and parsed, never Configuration.GetValue<bool>. A variable
// set to the empty string arrives as "" rather than null, and GetValue<bool>
// throws InvalidOperationException on it — which turns a stray key into a
// failed pre-upgrade hook and a blocked release. TryParse answers the same way
// for "", for null, for "1" and for "yes": do not seed.
bool requested = bool.TryParse(builder.Configuration["Seed:Enabled"], out bool enabled) && enabled;

if (requested && builder.Environment.IsDevelopment())
    builder.Services.AddScoped<OrderingSeeder>();

// src/Services/Ordering/Ordering.Migrator/MigrationRunner.cs. The seeder joins
// the primary constructor with a DEFAULT VALUE, and the `= null` is the whole
// of what makes it optional — see the trap below. The runner is otherwise §7.4's:
// MigrateAsync, a log line and an exit code.
public sealed class MigrationRunner(
    OrderingDbContext db,
    ILogger<MigrationRunner> logger,
    OrderingSeeder? seeder = null)

// RunAsync, after MigrateAsync returns. The log line is not decoration: a gate
// that fails closed in silence is indistinguishable from a seeder that is
// broken, and it is the seeder a developer debugs.
if (seeder is null)
    NotSeeding(logger, null);
else
    await seeder.SeedAsync(ct);
```

> **Trap — a nullable reference type is not an optional dependency.**
> `OrderingSeeder?` is an annotation the compiler reads and
> `Microsoft.Extensions.DependencyInjection` never does. A constructor parameter
> whose service is unregistered and which carries **no default value** makes
> resolving `MigrationRunner` throw `InvalidOperationException` — *unable to
> resolve service for type `OrderingSeeder` while attempting to activate
> `MigrationRunner`* — so the migrator that must not seed becomes the migrator
> that cannot start, and the gate fails the pre-upgrade hook *before* the
> migration it was guarding. That is the opposite of failing closed: the
> conditional registration above is only safe because the container falls back
> to a parameter's default value when it cannot resolve one, which is what
> `= null` supplies. Resolving it explicitly with
> `IServiceProvider.GetService<OrderingSeeder>()` is the other correct spelling
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
> the seeder — the gate is silent and the seeder is the visible half. §14.1's
> two migrator services set no environment name at all today, so the PR that
> writes the seeder gives them `DOTNET_ENVIRONMENT: Development` beside their
> connection strings. That line is the local half of the same switch and
> belongs in the same change as the guard, rather than being discovered by
> whoever runs `docker compose up` next.

**Turning seeding on has to be a diff, and today there is no diff that would do
it.** The shared migration-Job template renders exactly one `env` entry — the
migrator connection string of §7.1 — so `Seed__Enabled` has no route into that
container. Enabling it in a cluster would mean editing the library chart's
template *and* a values file — two lines in a reviewed pull request, rather
than a variable somebody exports onto a namespace. §15.3 carries the chart
half of that rule.

**A seeder creates rows and not principals.** §14.1's realm is imported by
Keycloak itself, from `deploy/compose/keycloak/realm-export.json` under
`start-dev --import-realm` — a container and a mode that exist in Compose and
nowhere else. Nothing in a migrator has ever created a subject, and a demo
principal in a production identity store is an account with a published
password. Seed the databases the gate above protects, and leave identity to an
import no deployed artefact reaches.

---

[← §13 Observability](13-observability.md) · [Index](README.md) · [§15 CI/CD →](15-cicd-deployment.md)
