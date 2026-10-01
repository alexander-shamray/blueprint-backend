# Appendix D — Names the solution does not declare

Code samples in this document are excerpts, not compilable units, and most of
what they name is code. A type or member declared under `src/` or `tests/` is
found with one `rg`, and the compiler answers for every sample that is also
code, so neither is listed here. This appendix holds only what neither can
answer for: the project-specific names a sample references that the solution
does not yet declare, so a reader can tell an elision from a defect.

**The rule: a project-specific name a sample references — type *or* member —
that `src/` and `tests/` do not declare must appear here.** If it is in
neither, it is a defect rather than an omission. Framework and library names
are out of scope (D.2); so are members of types the solution declares, since
those are visible at their declaration. A name here that the solution comes to
declare leaves this table in the change that declares it, and nothing else
about that change is recorded here — the type's own doc comments, the chapter
section that states its rule, and the ADR that decided it are the owners, and
this appendix cites and never restates them.

## D.1 Referenced and not yet declared

| Name | Section | What it is |
|---|---|---|
| `GetOrderSummariesQuery`, `GetOrderSummariesHandler`, `OrderSummaryDto`, `SummaryProduct` | §6.5, §6.6 | The worked query slice, shown at level 1 and rewritten in place at level 2. The query carries `Cursor` and `Limit` only, the subject being bound from `ICurrentUser` (§11.4); `SummaryProduct` is composed on read from `ordering.Products` |
| `SummaryRow` | §6.6 | The Dapper row shape behind the level-2 summaries query, `Products` read as JSON |
| `GetProductDetailQuery`, `GetProductDetailHandler`, `ProductDetailDto`, `ProductSql` | [§8.2](08-caching-redis.md) | Catalog's cached read slice, the `HybridCache` worked example |
| `PriceChangedCacheInvalidator` | §8.4 | The cache-only `PriceChanged` handler, which arrives with the first cached projection of Catalog data; §8.4 says so beside the sample |
| `GetConfiguredOptions` | [§9.7](09-messaging.md) | The helper §9.7's resilience sample calls to read the options under assertion; `ResilienceHierarchyTests` names it as the thing it leaves undefined |
| `OrderBuilder`, `AddressBuilder`, `CommandBuilder`, `SeedData` | [§12.3](12-test-strategy.md), §12.4 | The test data builders those sections' samples call |
| `TestCurrentUser`, `Principals` | §12.4 | The scoped `ICurrentUser` double and the principals a test states, printed in full by §12.4 and not yet built: `Ordering.TestSupport` declares neither, and the ownership tests state a principal through `TestAuthHandler`'s headers instead |
| `ServiceFixture.DispatchAsync` | §12.4 | The fixture seam §12.4 prints for reaching a handler below HTTP with a stated principal. Not yet on either fixture; `SagaCommandHandlerTests` carries a private helper that opens a scope and states no principal |

## D.2 Framework and library types

Not listed individually: ASP.NET Core, EF Core, MassTransit,
StackExchange.Redis, Dapper, Polly, OpenTelemetry, YARP, xUnit, Shouldly,
NSubstitute, Testcontainers, Respawn, Scrutor, FluentValidation, NetArchTest
and Aspire types are assumed — including the ones the samples name outright,
such as `TestResult` (§4.2), `IProxyStateLookup` ([§12.4](12-test-strategy.md))
and `IResourceBuilder<ProjectResource>` (§14.2). A library type a sample names
belongs to a package with a row in [Appendix B](appendix-b-licences.md), and
nothing here.

**The base class library and `Microsoft.Extensions.*` are assumed on the same
terms**, and the family is named here rather than left implicit because a rule
that lists fifteen library names reads as exhaustive. `IServiceCollection`,
`IHostedService`, `IOptions<T>`, `ILogger<T>`, `Meter` and `IMeterFactory`
(§13.3), and `MemoryCache` (§13.6) are all in it.

One of those names collides: xunit.v3 declares its own `Xunit.TestResult`, so
a test file holding §4.2's gates aliases the one it means —
`using TestResult = NetArchTest.Rules.TestResult;`.

---

[← Appendix C](appendix-c-delivery-plan.md) · [Index](README.md)
