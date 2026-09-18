# Appendix B — Dependency licence register

Between 2024 and 2026 several long-standing free .NET libraries moved to
commercial licences. The register below is the state at the time of writing;
**verify before adopting**, because this is exactly the category of fact that
goes stale.

**The backticked names in the Package column are read by a machine.** They are
NuGet package identities, and the licence gate PR-01 ships
(`.github/licence-gate/`) matches them against `Directory.Packages.props` — a
pin with no identity here fails the build before anything compiles
([§15.1](15-cicd-deployment.md)). The props file, not the transcription of it in
[§4.4](04-solution-structure.md): the gate checks that the two agree, but it is
the file CI restores that decides what a licence obligation is.

**And not that file alone.** Central pinning is a convention the build follows,
not a constraint it enforces, so the gate reads every `.csproj`, `.props` and
`.targets` as well. A `PackageReference` carrying its own `Version` or a
`VersionOverride`, and a project setting `ManagePackageVersionsCentrally` to
anything but `true`, each restore a package this register was never asked
about — all three ordinary MSBuild, and none of them writing a `PackageVersion`
element anywhere. **A register cannot disagree with a pin it never sees**,
which is why the gate's subject is the whole repository rather than one file in
it.

**The imported files are in that list for the reason the projects are, one
scope wider.** A `PackageReference` written into `Directory.Build.props`
reaches *every* project at once, so a scan that stopped at the projects would
have closed the narrow spelling of this defect and left the wide one standing —
the shape §4.2's gates keep being caught by, arriving in the check written to
close it.

So a product named in prose and a package named in backticks are different
claims: prose says what the thing is, backticks say what `restore` will resolve.
Adding a dependency means adding its identity here, not just its name.

## Chosen — free for commercial use

| Package | Licence | Role |
|---|---|---|
| ASP.NET Core, EF Core (`Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.SqlServer`), YARP (`Yarp.ReverseProxy`), HybridCache (`Microsoft.Extensions.Caching.Hybrid`) | MIT | Framework. ASP.NET Core is the one name in this row with no identity beside it, because it is a framework reference rather than a package and has nothing to pin |
| `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` | MIT | The container and logging contracts `Common.Application` compiles against ([§6.2](06-cqrs.md), [§13.3](13-observability.md)), and since PR-14 the logging one is `Common.Infrastructure`'s too — `OutboxDispatcher`'s `LoggerMessage.Define` delegates, which ADR-019's CA1848 requires on a loop running twice a second ([§9.4](09-messaging.md)). Both ride in ASP.NET Core's shared framework, which is why the row above needs no identity for them — but neither building block takes a framework reference ([§4.2](04-solution-structure.md)), so each pays for them as packages |
| `Microsoft.EntityFrameworkCore.Design` | MIT | The half of EF Core that `dotnet ef migrations add` runs in ([§7.4](07-persistence.md)). A separate identity from the framework row above rather than a name on it, because it is referenced with `PrivateAssets="all"` by each `*.Migrator` and ships in no image — a row that says "framework" would imply otherwise |
| `System.Security.Cryptography.Xml` | MIT | A transitive of the row above, pinned explicitly because the floor it declares resolves to a version carrying eight advisories and NU1903 fails the restore. Design-time only — it ships in no image — and registered anyway, on the rule that a pinned transitive is still a pin |
| `Microsoft.Extensions.Configuration.Abstractions` | MIT | `IConfiguration`, named in each `AddXInfrastructure` signature (§4.2). Registered on the abstractions row's terms: it rides in the shared framework, and `*.Infrastructure` is not a web project, so it pays for the contract as a package |
| `Microsoft.Extensions.Hosting` | MIT | The generic host each `*.Migrator` builds (§7.4). The one project shape in this blueprint that is neither a web host nor a library, so it is the one that pays for hosting as a package |
| `Microsoft.Extensions.Hosting.Abstractions` | MIT | `IHostEnvironment`, the single source of `RedisKeys`' key prefix ([§8.3](08-caching-redis.md)). Rides in the shared framework and in the `Hosting` meta-package above, but `Common.Infrastructure` takes neither, so it pays for the contract as a package — the abstractions rows' terms again |
| `Microsoft.Extensions.Options` | MIT | `AddOptions<RedisCacheOptions>().Configure` — §8.2's `InstanceName` and connection reuse are options configuration, called directly by `Common.Infrastructure`, so the assembly is referenced directly |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | MIT | HybridCache's L2 ([§8.2](08-caching-redis.md)): `AddStackExchangeRedisCache` and `RedisCacheOptions`. The `IDistributedCache` implementation over `StackExchange.Redis` — a separate identity from both its neighbours, because it is the package that actually stores an entry in Redis and neither of them is |
| `Microsoft.Extensions.Caching.Memory` | MIT | `MemoryCache`, the five-second snapshot `OutboxStats` holds its three aggregate queries in ([§13.6](13-observability.md)). `Microsoft.Extensions.Caching.Hybrid` carries it transitively at the same version; the explicit pin exists because `Ordering.Infrastructure` names the type directly, on `Microsoft.Data.SqlClient`'s terms two rows down. **Not an L1 in §8's sense** — it stores no domain data, crosses no process and is invalidated by nothing, so §8.1's namespace and TTL rules do not reach it |
| `Dapper` | Apache 2.0 | Read-side data access ([§6.5](06-cqrs.md)), the raw writes `IUnitOfWork.ExecuteRawAsync` makes on the command's own transaction (§6.3), the outbox dispatcher's claim, complete and fail statements in `Common.Infrastructure` ([§9.4](09-messaging.md)), and the retention purge's three `DELETE`s beside them, two of them since PR-15 and the third with §8.5's marker ([§9.5](09-messaging.md)) — the last two being neither read nor command, running on a connection of their own outside any transaction. All four named, because a register row saying "read-side" reads as an audit of where the package may appear, and a use this row does not list would then look unregistered |
| `Microsoft.Data.SqlClient` | MIT | `SqlConnection`, constructed by each service's `SqlConnectionFactory` behind §6.5's `IDbConnectionFactory`. EF's SqlServer provider carries it transitively at the same version; the explicit pin exists because `*.Infrastructure` names the type directly, and referencing what is actually used keeps this register honest |
| MassTransit **8.x** (`MassTransit`, `MassTransit.RabbitMQ`, `MassTransit.EntityFrameworkCore`) | Apache 2.0 | Messaging. Three identities on the health-check row's terms: the transport package is what each `*.Infrastructure` references, and the core package is referenced directly by **`Common.Infrastructure`** since PR-14, for the `IPublishEndpoint` the outbox dispatcher publishes the Broker lane through ([§9.4](09-messaging.md)) — the first common code to name a MassTransit type — and by four test projects that use the in-memory harness and no transport, which is core API and must not claim one: **`Catalog.Api.Tests`** and **`Ordering.Api.Tests`** for the registration smokes, **`Common.Infrastructure.Tests`** since PR-15 for §9.4's two consumers, and **`Ordering.Application.Tests`** since PR-21 for §12.5's saga suite. Enumerated rather than counted, and that is this row's own lesson applied to itself: it said "three times" while five projects referenced it, having been written when the smoke was one project and never recounted when it became two. A role that reads as an audit of where a package may appear has to list every place it does — and the licence gate cannot enforce it, because the *identity* was already registered and only the role went stale, which is exactly how a number survives four PRs that each made it wronger. The persistence integration is PR-21's, and its consumers are enumerated rather than counted — this row's own lesson applied to the row's other half. **`Ordering.Infrastructure`** names §9.6's `EntityFrameworkRepository` and its `ConcurrencyMode`, and the transactional outbox [ADR-032](adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md) puts on the saga's receive endpoint — `AddEntityFrameworkOutbox<OrderingDbContext>`, `UseEntityFrameworkOutbox<OrderingDbContext>` and the `AddTransactionalOutboxEntities()` that maps its three tables in `OrderingDbContext`. **`Ordering.Api.Tests`** names `EntityFrameworkOutboxOptions<OrderingDbContext>` and nothing else from this package: it resolves those options to assert ADR-032's `IsolationLevel.Serializable`, while the other two MassTransit types that suite reaches for — `IOutboxContextFactory<T>` in `MassTransit.Middleware` and `IBusOutboxNotification` in `MassTransit.Middleware.Outbox` — belong to the **core** assembly and are therefore the identity above rather than this one. **`Ordering.TestSupport`** names `InboxCleanupService<OrderingDbContext>`, which `OrderingApiFactory` removes from the test host. Every use is the saga's, and Ordering holds the only saga in the solution, so no other service may claim this identity. **Each correction arrived on this row's own lesson and none was learned unaided**: the sentence said "referenced once" while two projects named the types, then said "two" while three did and credited one project's type to another — exactly the way it once said "three times" against five. Copilot caught both on #154, which is the answer to the question this row keeps raising about itself: the licence gate cannot, because the *identity* was already registered and only the role went stale |
| `StackExchange.Redis` | MIT | Redis client |
| `FluentValidation` | Apache 2.0 | Input validation |
| `FluentValidation.DependencyInjectionExtensions` | Apache 2.0 | `AddValidatorsFromAssemblyContaining` — the registration line in each `AddXApplication` ([§4.2](04-solution-structure.md)). A separate identity from the row above because it is a separate package: the scan does not ship in `FluentValidation` itself, and a register matching on prefixes is the drift the health-check row below names |
| `Scrutor` | MIT | Assembly scanning for handler registration ([§6.2](06-cqrs.md)) |
| OpenTelemetry .NET (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`, `OpenTelemetry.Instrumentation.EntityFrameworkCore`, `OpenTelemetry.Instrumentation.StackExchangeRedis`) | Apache 2.0 | Telemetry, including logging ([§13.2](13-observability.md)) — there is no separate logging library. All seven written out rather than abbreviated to a prefix, for the reason the health-check row below gives |
| Polly (`Microsoft.Extensions.Http.Resilience`) | MIT | Resilience |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | MIT | §11.3's token validation, registered by `Common.Web` for every host because every service re-validates ([§11.2](11-identity-authorization.md)). Registered here on the abstractions rows' terms inverted: this one does **not** ride in `Microsoft.AspNetCore.App`, which carries the authentication abstractions and the cookie handler and has not carried a JWT handler since ASP.NET Core 3.0 — so the one building block that does take a framework reference still pays for this as a package |
| `Microsoft.AspNetCore.OpenApi` | MIT | The OpenAPI document each service host serves ([Appendix C](appendix-c-delivery-plan.md), PR-07) — the framework's own generator, document only, no UI |
| `Microsoft.OpenApi` | MIT | The generator's document model — a transitive dependency pinned explicitly, because the floor the package above declares resolves to a version with a known vulnerability and NU1903 fails the restore. A pinned transitive is still a pin, so it is registered like any other |
| `AspNetCore.HealthChecks.SqlServer`, `AspNetCore.HealthChecks.Redis` (Xabaril) | Apache 2.0 | The readiness checks §13.5 registers. Written out in full rather than abbreviated to `.Redis`, so a text comparison against the pins in `Directory.Packages.props` matches on the whole name — an abbreviation here is satisfied by `StackExchange.Redis` and the check passes while the row is missing. The one third-party runtime dependency that is easy to leave off this table, because "health checks" sound like framework and are not. **No `.Rabbitmq` sibling, deliberately** (PR-13): the broker's readiness check is the one MassTransit itself registers, and the Xabaril check would need an `IConnection` nothing registers — §13.5 carries the argument |
| gRPC for .NET (`Grpc.AspNetCore`, `Grpc.Net.ClientFactory`, `Grpc.Tools`, `Google.Protobuf`) | Apache 2.0 / BSD-3 | The BFF's synchronous hop and its `.proto` contract ([§9.7](09-messaging.md)) |
| `Microsoft.AspNetCore.Mvc.Testing` | MIT | `WebApplicationFactory` for the API-contract tests ([§12.4](12-test-strategy.md)) |
| `Microsoft.AspNetCore.TestHost` | MIT | `TestServer` for the host building blocks, which have no entry point for `WebApplicationFactory` to find ([§4.1](04-solution-structure.md)) |
| xUnit v3 (`xunit.v3`, `xunit.runner.visualstudio`, `xunit.v3.extensibility.core`) | Apache 2.0 | Test framework, and the VSTest adapter through which `Microsoft.NET.Test.Sdk` discovers its tests. Three identities rather than one: the adapter is a separate package and omitting it is silent — the build succeeds, `dotnet test` reports no tests and exits zero ([§12.1](12-test-strategy.md)) — and the third is the fixture contract alone, `IAsyncLifetime` and `TestContext`, for `*.TestSupport` libraries, which `xunit.v3` itself refuses to build ([§12.4](12-test-strategy.md)) |
| `NSubstitute` | BSD-3 | Mocking |
| `Shouldly` *or* `AwesomeAssertions` | BSD-3 / Apache 2.0 | Assertions. An either/or row: only the chosen library is pinned in §4.4, and the licence gate excludes the other rather than reporting it as a dropped pin |
| Testcontainers for .NET (`Testcontainers.MsSql`, `Testcontainers.Redis`, `Testcontainers.RabbitMq`, `Testcontainers.Keycloak`) | MIT | Integration test infrastructure. The Keycloak module is [§11.5](11-identity-authorization.md)'s suite, and the only place the solution runs an identity provider |
| `System.IdentityModel.Tokens.Jwt` | MIT | `JwtSecurityTokenHandler`, which [§11.5](11-identity-authorization.md)'s suite reads the `aud` claim with. A transitive of the JWT bearer handler, pinned because a project names the type |
| `SSH.NET` | MIT | The SSH transport behind Testcontainers' port forwarding — a transitive dependency pinned explicitly, on the same terms as `Microsoft.OpenApi` above: the floor the row above declares resolves to a version carrying GHSA-q939-rpr3-3284 and NU1903 fails the restore. Registered because a pinned transitive is still a pin, not because anything here opens an SSH connection |
| `Respawn` | MIT | Test database reset |
| `WireMock.Net` | Apache 2.0 | HTTP stubbing |
| `Microsoft.NET.Test.Sdk` | MIT | The test host every test project needs; pinned because a major bump changes discovery |
| `Microsoft.Extensions.TimeProvider.Testing` | MIT | `FakeTimeProvider` — the clock seam §12.7 requires |
| `Microsoft.Extensions.DependencyInjection` | MIT | `ServiceCollection` for the registration and behaviour-ordering tests of [§6.2](06-cqrs.md) and [§6.3](06-cqrs.md). A separate identity from the abstractions row above, and not a redundant one: that package is contracts, with no container in it to build |
| `Microsoft.Extensions.Configuration` | MIT | `ConfigurationBuilder` and `AddInMemoryCollection` for `AddRedisConnections`' tests ([§8.2](08-caching-redis.md)), which build a real configuration rather than substitute the contract — the container row's argument, one abstraction over |
| `OpenTelemetry.Exporter.InMemory` | Apache 2.0 | Reads log records, metrics and spans back out of a real pipeline for the observability tests — §13.4's redaction tests, which that chapter prints one of, the meter-coverage tests guarding §13.2's meter list in `Common.Web.Tests`, and the Redis client-span tests in `Common.Infrastructure.Tests` proving `AddRedisConnections` instruments its own connections ([§13.2](13-observability.md)). A separate row from the runtime OpenTelemetry entry above rather than an eighth name on it: that row's role is the telemetry every host exports, and this package is referenced by no project under `src/` |
| `NetArchTest.Rules` | MIT | The architecture gates ([§4.2](04-solution-structure.md)) that PR-07 makes a build failure rather than a review comment ([Appendix C](appendix-c-delivery-plan.md)) |
| `Aspire.Hosting.*` (`AppHost`, `SqlServer`, `Redis`, `RabbitMQ`, `Keycloak`) | MIT | Topology for the optional AppHost of [§14.2](14-local-development.md). Registered ahead of use: Compose is the baseline (§14.1) and Aspire is not adopted. **If** it is, `src/AppHost` is the only project taking these, which is what keeps it deletable. **Not pinned in [§4.4](04-solution-structure.md)** — see the carve-out there; adopting Aspire means adding the pins in the same change |
| `Aspire.*` client integrations (one per resource a service consumes) | MIT | The service-side half of §14.2. Registered ahead of use: **if** Aspire is adopted each service takes the integrations for its own resources, which is why backing it out costs a line per resource per service rather than deleting one project. Unpinned on the same terms as the row above |
| Keycloak | Apache 2.0 | Identity provider |
| RabbitMQ | MPL 2.0 | Message broker |
| RabbitMQ delayed message exchange plugin **4.1.0** | MPL 2.0 | The delayed exchange [ADR-021](adr/ADR-021-saga-timeouts-are-scheduled-by-the-broker.md) schedules §9.6's saga timeouts through — a **separate repository** from the broker above, shipping in no official image, which is why `deploy/compose/rabbitmq` builds one. **Named in prose rather than in backticks, and the gate is why**: a backticked identity in this table is a NuGet package the gate expects to find pinned in `Directory.Packages.props`, and this is an `.ez` a Dockerfile fetches. Written in backticks it fails the build as "registered in Appendix B and pinned nowhere" — measured. It joins RabbitMQ, Keycloak and SQL Server as infrastructure the register carries by the rule rather than by the tool, and it is the first row here that no automation would have demanded. Pinned by version *and* by SHA-256 in the Dockerfile; the version is stated here because the plugin is built against a broker line and a mismatch is a build failure rather than a dependency bump |
| OpenTelemetry Collector (contrib) | Apache 2.0 | Local and CI telemetry pipeline — the `otel-collector` container of [§14.1](14-local-development.md) |

## Avoided — commercial, with the replacement used here

| Package | Change | Replacement in this blueprint |
|---|---|---|
| **MediatR** v13+ | Commercial from 2025 | Hand-rolled dispatcher (§6.2) |
| **AutoMapper** v15+ | Commercial from 2025 | Explicit mapping, by hand (§9.3). A source generator such as Mapperly is the alternative, and is not pinned here because nothing in the blueprint uses one |
| **MassTransit** v9 | Commercial from 2026, $400–1,200/month | Pinned to v8 (Apache 2.0) |
| **FluentAssertions** v8+ | Per-developer-per-year from 2025 | Shouldly, or AwesomeAssertions (a fork of v7) |

## Requires a licence review

| Component | Condition |
|---|---|
| SQL Server | Per-core or CAL licensing; Developer Edition is free for non-production only |
| Duende IdentityServer | Free below a revenue threshold; commercial above it |
| Redis | Redis 7.4+ is under RSALv2/SSPLv1. Valkey (BSD) is a drop-in fork if the terms are a problem |
| Grafana OTel-LGTM (`grafana/otel-lgtm` image) | AGPL 3.0 for Grafana, Loki, Tempo and Mimir (the Prometheus-compatible store inside the image). Development and CI only ([§14.1](14-local-development.md)); never deployed, so no distribution or network-service obligation arises |

The first table is enforced by
[`.github/licence-gate/`](../../.github/licence-gate/README.md), whose README
owns what it refuses. Where a package really is offered under either of two
licences, its row names the half taken here, because the gate cannot tell a
disjunction from a conjunction.

Discovering a licence obligation at renewal time is considerably more expensive
than discovering it at build time.

**The table above is beyond its reach, and deliberately so.** SQL Server,
Duende, Redis and the Grafana OTel-LGTM image are products rather than
packages — nothing restores them, so nothing reading `Directory.Packages.props`
can see them, and the client libraries that talk to them sit in the first table
under their own licences. That boundary is a review obligation and there is no
tooling behind it.

---

[← Appendix A](appendix-a-adrs.md) · [Index](README.md) · [Appendix C →](appendix-c-delivery-plan.md)
