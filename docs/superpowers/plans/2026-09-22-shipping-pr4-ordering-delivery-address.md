# Shipping PR-4 — a delivery address is read under orders:delivery-address — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Ordering the gRPC method ADR-052 decides —
`DeliveryAddresses.Get`, on a second HTTP/2-only port, behind
`orders:delivery-address` — and give the realm the `shipping-worker` client
that holds that one role, so the reader Shipping's PR-5 builds has an owner to
ask. Nothing calls it yet, and no Shipping path moves.

**Architecture:** Ordering grows the transport surface Catalog already has, in
Catalog's shape: a `.proto` the service owns, a `Grpc/` adapter that parses,
dispatches and projects, a second Kestrel endpoint declared `Http2` because a
cleartext endpoint cannot serve HTTP/1.1 and h2c at once, and §6.5's read side
behind it — one Dapper query over `ordering.Orders`. The differences from
Catalog are both ADR-052's: the method requires a **permission** rather than
bare authentication, because the read crosses subjects and an authenticated
caller alone would let the BFF's client make it; and the ownership check is
skipped on purpose, because a service account's subject owns no order. The
realm half is a client role on `commerce-api`, a confidential service-account
client that takes `commerce-api` as a **default** scope, and the service
account user that holds the role — the only route by which the `permission`
claim's mapper emits anything for a host.

**Tech Stack:** `Grpc.AspNetCore` (pinned, used by Catalog), Dapper (pinned,
used by Catalog and Payments), Keycloak realm export, stdlib Python 3.12 for
the realm gate, xUnit with Shouldly and Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-22-shipping-service-design.md`,
sections 2 (the bullet that gives Ordering the method), 3 (the PR's row and its
order), 9 (the address port's contract, read from the server's side), 10 (the
client secret's places), 12 (PR-4's five tests) and 13 (the chapters that move).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Ordering/**`,
  `tests/Ordering.Api.Tests/**`, `tests/Common.Web.Tests/RealmImportTests.cs`,
  `tests/Web.Bff.Tests/**`, `tests/Gateway.Api.Tests/**`,
  `deploy/compose/keycloak/realm-export.json`, `deploy/compose/README.md`,
  `deploy/keycloak/realm_check.py`,
  `deploy/keycloak/test_realm_check.py`, `deploy/keycloak/README.md`,
  `deploy/helm/ordering/values.yaml`, `deploy/helm/smoke.sh`,
  `deploy/helm/catalog/Chart.yaml`, `deploy/helm/catalog/values.yaml`,
  `.github/secret-scan/allowed/deploy.txt`,
  `.github/secret-scan/allowed/tests.txt`,
  `docs/backend-architecture/11-identity-authorization.md`,
  `docs/backend-architecture/15-cicd-deployment.md`, `docs/secrets.md`,
  `docs/repo-map.md`, `CLAUDE.md`
  — paths only, comma-separated, no prose inside the cell and no trailing
  stop: the gate strips a token's backticks only when the token ends in one,
  and refuses the row outright otherwise.
  Why each: Ordering's slice and the four
  suites that read what it changes are A — `tests/**` is Class A's, and
  `RealmImportTests` is the one building-block test that owns the realm's
  closed sets; the realm, the realm gate and the README that owns its claim,
  the Compose README that owns the host-run port recipe, the two charts,
  `smoke.sh`'s listener comparison, the two secret-scan entries, the three
  chapters and the two maps are D. **The E letter is owed for three new
  package references**: `Ordering.Api.csproj` takes `Grpc.AspNetCore`,
  `Ordering.Application.csproj` takes `Dapper` and
  `tests/Ordering.Api.Tests/Ordering.Api.Tests.csproj` takes
  `Grpc.Net.ClientFactory`, none of them with a `Version=`. A reference a
  project did not have is `docs/change-locality.md`'s "add a package" whether
  or not the pin already exists, and PR-2 spells the same edit the same way.
  No pin moves in `Directory.Packages.props`, no project joins
  `Platform.slnx`, and no Appendix B row moves, because `Grpc.AspNetCore`,
  `Grpc.Net.ClientFactory` and `Dapper` are all pinned and all already
  registered. The touch set does not grow for the letter: the three files sit
  inside `src/Services/**` and `tests/**`, which E reaches again as
  `**/*.csproj`.
- **`A+D+E` is the one three-member cell the class row accepts**, and
  `.github/locality-gate/locality_gate.py` admits it today, so no gate change
  is owed and the row is spelled exactly that way.
- Depends on **PR-3b having merged**, so `ITokenCache`, `CachingTokenClient`,
  `ClientCredentialsHandler` and `ServiceIdentityOptions` are already in
  `Common.Infrastructure` and no later PR has to move them out from under a
  second consumer. Nothing in this plan compiles against them: PR-4 is the
  **server** side, and the client is PR-5's. It depends on **no Shipping
  path**, which is what lets a second session take it beside PR-1 and PR-2
  (spec, section 3).
- **Ordering serves the method; nothing dials it in this PR.** No
  `AddressSource__BaseUrl`, no `Identity__Client__*` on a Shipping host, no
  Compose edit under `deploy/compose/services/` — spec section 10 assigns
  those keys to PR-5, and a Compose unit under `services/` is a Shipping path
  this PR may not touch.
- **ADR-052 is the governing record**, and its closing table is the list of
  places that must move. Every row of it this PR turns red is named in a task
  below with the task that turns it green; every row it leaves for a later PR
  is named in *Self-review*.
- Comments say why and cite the owner — a section, an ADR or a symbol, never a
  pull request or a test — and no comment block runs past ten lines. **The
  comment gate judges an edited line as its whole block**, so a doc comment
  this PR touches has to come out at ten lines or fewer with no `<b>` and no
  `**…**`, however long it was before; the blocks it does not touch stay as
  they are. Explicit
  local types, file-scoped namespaces with a blank line after, braces on two
  or more statements, 120 columns for code and 80 for prose. British spelling.
  `py -3.12`, never `python`.
- Every step that adds behaviour writes its test first.

---

### Task 1: `delivery_addresses.proto` and Ordering's second port

**Files:**
- Create: `src/Services/Ordering/Ordering.Api/Protos/delivery_addresses.proto`
- Create: `src/Services/Ordering/Ordering.Api/appsettings.json` — the
  service's
  first, and the second in any service
- Modify: `src/Services/Ordering/Ordering.Api/Ordering.Api.csproj` —
  `Grpc.AspNetCore` and the `Protobuf` item
- Modify: `deploy/compose/README.md` — the uniqueness claim over the host-run
  recipe, and Ordering's own two exports
- Test: `tests/Ordering.Api.Tests/KestrelEndpointTests.cs`
- Modify: `tests/Ordering.Api.Tests/Ordering.Api.Tests.csproj` —
  `Grpc.Net.ClientFactory`, for Task 4's channel

**Interfaces:**
- Produces, generated from the `.proto` into `Ordering.Delivery.V1`:

```csharp
// The generated halves, named here because every later task spells them.
public sealed class GetDeliveryAddressRequest { public string OrderId { get; set; } }

public sealed class GetDeliveryAddressReply
{
    public string CustomerId { get; set; }
    public string Line1 { get; set; }
    public string Line2 { get; set; }
    public string City { get; set; }
    public string PostCode { get; set; }
    public string Country { get; set; }
}

public static class DeliveryAddresses
{
    public abstract class DeliveryAddressesBase { /* Get */ }
    public sealed class DeliveryAddressesClient { /* GetAsync */ }
}
```

- [ ] **Step 1: Write the failing endpoint test**

`tests/Ordering.Api.Tests/KestrelEndpointTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// The two Kestrel endpoints, read off the host's own configuration rather
/// than off the file: that is the text the server binds (§9.7, ADR-052).
/// </summary>
/// <remarks>
/// Catalog's <c>appsettings.json</c> carries the measurement: a cleartext
/// endpoint at <c>Http1AndHttp2</c> refuses a client asking for HTTP/2
/// exactly, and an <c>Http2</c>-only one refuses HTTP/1.1. The REST entry is
/// asserted beside it because this section overrides the image's own ports.
/// </remarks>
public sealed class KestrelEndpointTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IConfiguration Configuration => factory.Services.GetRequiredService<IConfiguration>();

    [Fact]
    public void The_rest_surface_stays_on_8080_over_http1()
    {
        Configuration["Kestrel:Endpoints:Rest:Url"].ShouldBe("http://0.0.0.0:8080");
        Configuration["Kestrel:Endpoints:Rest:Protocols"].ShouldBe("Http1");
    }

    [Fact]
    public void The_grpc_surface_is_8081_and_http2_only()
    {
        Configuration["Kestrel:Endpoints:Grpc:Url"].ShouldBe("http://0.0.0.0:8081");
        Configuration["Kestrel:Endpoints:Grpc:Protocols"].ShouldBe(
            "Http2",
            "a cleartext endpoint at Http1AndHttp2 refuses a client asking for HTTP/2 exactly, " +
            "so the address read would fail at the connection (§9.7)");
    }
}
```

- [ ] **Step 2: Run to see it fail**

```bash
dotnet test tests/Ordering.Api.Tests --filter "FullyQualifiedName~KestrelEndpointTests"
```

Expected: both tests fail, each on its first `ShouldBe` and each reporting
`null` — the host has no `appsettings.json`, so the section does not exist.
Shouldly throws on the first assertion in a test, so the second `ShouldBe` in
each is not reached until the first passes.

- [ ] **Step 3: Write the proto**

`Protos/delivery_addresses.proto`:

```proto
syntax = "proto3";

// Ordering owns this file, because Ordering serves the RPC — pricing.proto's
// rule, one service over. Shipping LINKS it rather than copying it (§4.3), as
// Web.Bff links pricing.proto: one contract, two generated halves, and no
// assembly crosses the boundary.
package ordering.delivery.v1;

// Both halves generate into this namespace, in their own assemblies. The
// method's path is /ordering.delivery.v1.DeliveryAddresses/Get, which is what
// the gateway's route file must not match (ADR-052).
option csharp_namespace = "Ordering.Delivery.V1";

// The platform's second synchronous hop, and the first one no user is waiting
// on (ADR-052): a Shipping worker claims a shipment row under a lease, asks
// for that order's address and commits the answer. It is not a screen's call
// and it is not made inside a consumer, so ADR-017's one-hop exception is not
// spent by it.
service DeliveryAddresses {
  // One order, never a batch. The caller resolves one shipment per pass, so a
  // fan-out would be a request shape nothing produces — the opposite of
  // GetPrices, where an order form holds a handful of products at once.
  rpc Get (GetDeliveryAddressRequest) returns (GetDeliveryAddressReply);
}

message GetDeliveryAddressRequest {
  // A GUID in its canonical text form, for pricing.proto's reason: 36 readable
  // bytes beat 16 unreadable ones in every log line and every grpcurl
  // transcript this call will be debugged from.
  string order_id = 1;
}

message GetDeliveryAddressReply {
  // The order's customer, carried for the reader's erasure row and for nothing
  // else (ADR-052). Shipping keys its copy by the order and holds this beside
  // it so §11.7's erasure consumer can delete by subject; the shipment's own
  // record holds neither.
  string customer_id = 1;

  // Address.Of's five parts, in its own spelling (§5.3). Nothing else of the
  // order travels: not its status, not its total, not its lines. A reader that
  // needed one of those would be reading an order rather than an address.
  string line1 = 2;

  // EMPTY, never absent: proto3 has no null string, and Address.Line2 is
  // optional — an order placed without a second line answers "" here, and a
  // reader stores that as the absence it is rather than as a blank line.
  string line2 = 3;
  string city = 4;
  string post_code = 5;

  // ISO 3166-1 alpha-2, upper-cased by Address.Of. SHAPE and not membership:
  // the producer checks two ASCII letters and deliberately not the assigned
  // set, so ZZ arrives here and whoever ships the parcel is the layer that
  // knows (Address's own remarks).
  string country = 6;
}
```

**`post_code`, not `postal_code`.** The two spell different C# properties —
the generator drops the underscore and capitalises what follows, so
`post_code` becomes `PostCode` and `postal_code` would become `PostalCode` —
and the field name is a contract both halves compile against, so it is named
here once and every later reference in this plan spells it the same. The
domain's property stays `PostalCode`, because identifiers keep their real
spelling, and Task 4's service maps it onto the reply's `PostCode` in the one
place the two vocabularies meet.

- [ ] **Step 4: The csproj and the settings file**

In `Ordering.Api.csproj`, beside `Microsoft.AspNetCore.OpenApi`:

```xml
    <!-- The server half of ADR-052's address read. Grpc.AspNetCore brings
         Grpc.Tools and Google.Protobuf with it, which is why neither is named
         here — Appendix B registers all four as one row because they ship and
         version as one thing. -->
    <PackageReference Include="Grpc.AspNetCore" />
```

and a new `ItemGroup`:

```xml
  <ItemGroup>
    <!-- Ordering owns the contract because Ordering serves it. Shipping
         compiles this same file as a Client, by link (§4.3).

         BOTH halves, not Server alone, and for Catalog.Api's reason: the suite
         drives the service over the real pipeline, which needs a client, and
         generating one in the test project would put a second copy of every
         message type in a compilation that already references this assembly —
         CS0436, an error under ADR-019. -->
    <Protobuf Include="Protos\delivery_addresses.proto" GrpcServices="Both" />
  </ItemGroup>
```

`Ordering.Api/appsettings.json`:

```jsonc
{
  // The second appsettings.json in a service, and it exists for Catalog's one
  // reason: ADR-052's gRPC method cannot share a port with §10.2's REST
  // surface, and the per-endpoint protocol is not something an environment
  // variable can say. Catalog.Api's own copy carries the measurement.
  //
  // This section OVERRIDES the container image's own port configuration:
  // ASPNETCORE_HTTP_PORTS and ASPNETCORE_URLS both lose to Kestrel:Endpoints
  // and neither produces a warning, so 8080 has to be declared here or it
  // stops existing — and §10.2's `ordering` cluster dials exactly that.
  "Kestrel": {
    "Endpoints": {
      // §10.2's cluster destination and the health probes. Http1 explicitly
      // rather than by default, so a reader comparing the two entries does not
      // have to know that cleartext Http1AndHttp2 behaves as Http1 anyway.
      "Rest": {
        "Url": "http://0.0.0.0:8080",
        "Protocols": "Http1"
      },
      // ADR-052's read, and nothing else reaches it. Not exposed by the
      // gateway (§10.2 routes HTTP), not published by the Compose block, and
      // not a second public surface — a cluster-internal port on the same
      // footing as the SQL one, which is why the RPC on it authorises (§11.2).
      "Grpc": {
        "Url": "http://0.0.0.0:8081",
        "Protocols": "Http2"
      }
    }
  }
}
```

In `Ordering.Api.Tests.csproj`, beside `NSubstitute`:

```xml
    <!-- GrpcChannel and Grpc.Core's StatusCode, for calling the gRPC server
         over loopback. Carried transitively through Ordering.Api; named here
         on the register's honesty rule, because a project that names a type
         declares the package rather than relying on a production csproj it
         does not control. -->
    <PackageReference Include="Grpc.Net.ClientFactory" />
```

- [ ] **Step 5: The host-run recipe in `deploy/compose/README.md`**

`deploy/compose/README.md` is where what a `Kestrel:Endpoints` section costs
outside the container is written down, and it is written as a fact about
Catalog alone. With step 4's settings file in place, a host-run Ordering binds
`0.0.0.0:8080` — the port Keycloak publishes — and dies on the message the
README already quotes, with nothing in the recipe to say why. The claim and
the recipe both move here, because step 4 is what makes them wrong.

The uniqueness claim first. Before:

```bash
# Catalog is the ONE service that pins its own ports, and on the host they
# have to move. Its appsettings.json declares two Kestrel endpoints — 8080 for
# REST and 8081 for §9.7's gRPC hop, because a cleartext port cannot serve
# HTTP/1.1 and h2c at once — and 8080 on the host belongs to Keycloak, so a
# host run without these two lines fails to bind.
```

After:

```bash
# Catalog and Ordering each pin their own ports, and on the host both have to
# move. Each declares two Kestrel endpoints — 8080 for REST and a second for
# its gRPC surface, because a cleartext port cannot serve HTTP/1.1 and h2c at
# once — and 8080 on the host belongs to Keycloak, so a host run without these
# two lines fails to bind.
```

Five lines to five, no emphasis, and the paragraph below it — the one naming
the *Failed to bind* message and why it does not say Keycloak and Catalog are
related — is untouched apart from the project it names. Before:

```markdown
because the address it names is Keycloak's and the project it names is
Catalog's, and nothing in that message says the two are related.
```

After:

```markdown
because the address it names is Keycloak's and the project it names is the
one being run, and nothing in that message says the two are related.
```

Then Ordering's own block, which today ends at `dotnet run`. Before:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Ordering='Server=localhost;Database=Ordering;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://ordering-svc:local-dev-ordering@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
dotnet run --project src/Services/Ordering/Ordering.Api
```

After:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Ordering='Server=localhost;Database=Ordering;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://ordering-svc:local-dev-ordering@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
# The same two exports Catalog needs, for the same reason and at its own
# numbers: 5101 is the port §14.1 already allocates this service, and 8082
# rather than 8081 because 8081 is where the block above puts Catalog's h2c
# listener — two host processes cannot both hold it.
export Kestrel__Endpoints__Rest__Url='http://localhost:5101'
export Kestrel__Endpoints__Grpc__Url='http://localhost:8082'
dotnet run --project src/Services/Ordering/Ordering.Api
```

`ReverseProxy__Clusters__ordering__Destinations__d1__Address` in the gateway's
own block further down already dials `http://localhost:5101/`, so the REST
export lands on the number that block was written against and nothing there
moves.

**`docker-compose.infra-only.yml`'s comment stays as it is**, and that is a
judgement rather than an oversight. It makes no uniqueness claim: it says
Catalog's `appsettings.json` declares `Kestrel:Endpoints` and that the
invocation belongs in `README.md` rather than in a second copy, both of which
this step leaves true. Touching it would put a block the comment gate then
judges whole back through a limit it does not meet today, for a sentence that
is not wrong.

- [ ] **Step 6: Run; commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Ordering.Api.Tests --filter "FullyQualifiedName~KestrelEndpointTests"
```

Expected: 0 warnings, 2 passing. The comment gate reads no `.md` file, so the
README's two rewritten blocks are held to `docs/style-guide.md`'s *Comments*
section by a reader and by nothing else — which is why each is written out
above at its finished length rather than left to be shortened later. Then:

```bash
git add src/Services/Ordering/Ordering.Api tests/Ordering.Api.Tests deploy/compose/README.md
git commit -m "feat(ordering): delivery_addresses.proto and an Http2-only second port"
```

---

### Task 2: `GetDeliveryAddressQuery`

**Files:**
- Modify: `src/Services/Ordering/Ordering.Application/Ordering.Application.csproj`
  — `<PackageReference Include="Dapper" />`, and the comment standing in its
  place cut
- Create: `Ordering.Application/Orders/GetDeliveryAddress/GetDeliveryAddressQuery.cs`
- Create: `.../GetDeliveryAddress/DeliveryAddressView.cs`
- Create: `.../GetDeliveryAddress/GetDeliveryAddressHandler.cs`

**Interfaces:**
- Consumes: `Common.Application.IQuery<T>`, `IQueryHandler<TQuery, TResult>`,
  `IDbConnectionFactory.Create()` — all as declared.
- Produces:

```csharp
public sealed record GetDeliveryAddressQuery(Guid OrderId) : IQuery<DeliveryAddressView?>;

public sealed record DeliveryAddressView(
    Guid CustomerId,
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string Country);
```

- [ ] **Step 1: Write the handler**

The query is tested over the transport in Task 4, which is where its SQL meets
the migrated schema; there is no in-memory stand-in for Dapper worth writing,
and the three answers it collapses are stated on the wire rather than in the
view.

`GetDeliveryAddressHandler.cs`:

```csharp
using System.Data;
using Common.Application;
using Dapper;

namespace Ordering.Application.Orders.GetDeliveryAddress;

/// <summary>
/// §6.5's read side over <c>ordering.Orders</c>: where one order ships, for
/// the worker ADR-052 gives the read to. No status, no total, no lines.
/// </summary>
/// <remarks>
/// No such order, a cancelled order and an order whose address erasure has
/// cleared all answer <c>null</c>, which ADR-052 makes the contract: the
/// client maps one status and never reads an order's state. A view that
/// distinguished them would put the distinction on the wire.
/// </remarks>
public sealed class GetDeliveryAddressHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetDeliveryAddressQuery, DeliveryAddressView?>
{
    // Cancelled is compared as text because §7.2 persists the status by name.
    // The filter is in the statement rather than in the branch below so the
    // cancelled row never leaves the database.
    private const string Sql =
        """
        SELECT o.CustomerId,
               o.ShipToLine1 AS Line1,
               o.ShipToLine2 AS Line2,
               o.ShipToCity AS City,
               o.ShipToPostalCode AS PostalCode,
               o.ShipToCountry AS Country
        FROM ordering.Orders o
        WHERE o.Id = @OrderId
            AND o.Status <> 'Cancelled';
        """;

    public async Task<DeliveryAddressView?> HandleAsync(GetDeliveryAddressQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        DeliveryAddressView? address = await connection.QuerySingleOrDefaultAsync<DeliveryAddressView>(
            new CommandDefinition(Sql, new { query.OrderId }, cancellationToken: ct));

        // The erasure case, designed against before the consumer that produces
        // it exists (ADR-052). §11.7's extension clears an erased subject's
        // address in place and leaves the order's own record whole, so the row
        // survives with nothing to ship to — and an answer of five blank
        // strings is a parcel addressed to nowhere rather than an absence.
        return string.IsNullOrWhiteSpace(address?.Line1) ? null : address;
    }
}
```

`GetDeliveryAddressQuery.cs` and `DeliveryAddressView.cs` as in *Interfaces*.
`DeliveryAddressView`'s summary: "The five parts of `Address` plus the order's
customer, which the reader keeps for erasure's sake alone (ADR-052)."

In `Ordering.Application.csproj`, the block that reads

```xml
    <!-- Dapper is not here yet: §6.5's read side uses it directly, and this
         project has no query handler to use it. It joins with the first
         one — an unused package reference is a claim this project would
         not be making. -->
```

is cut and replaced by the reference it was holding a place for:

```xml
    <!-- §6.5's read side, used directly by the first query handler this
         project has. Centrally pinned and already in Appendix B. -->
    <PackageReference Include="Dapper" />
```

- [ ] **Step 2: Build; commit with Task 4**

```bash
dotnet build Platform.slnx
```

Expected: 0 warnings. Nothing resolves the handler yet — `AddPluggableFrom`
scans this assembly for `IQueryHandler<,>` implementations, so registration is
automatic and the first resolution is Task 4's.

---

### Task 3: The realm's role, the `shipping-worker` client, and the gate

This task lands **before** the permission constant, and the order is
deliberate: `GrantablePermissionTests` reflects over `OrderingPermissions` and
asserts the realm can grant every name it finds, so a constant added before its
role would leave one commit red.

**Files:**
- Modify: `deploy/compose/keycloak/realm-export.json` — the
  `orders:delivery-address` client role, the `shipping-worker` client, its
  service-account user, and `web-bff`'s description
- Modify: `tests/Common.Web.Tests/RealmImportTests.cs` — the closed role set,
  the credentialed-client set, and a new assertion over the service account
- Modify: `tests/Web.Bff.Tests/RealmClientTests.cs` —
  `It_is_the_only_service_account_client_in_the_realm`, renamed and widened
- Modify: `deploy/keycloak/realm_check.py` — `WORKER_CLIENT`,
  `check_worker_client`, and the three fields the projection has to keep for it
- Modify: `deploy/keycloak/test_realm_check.py` — a `worker()` fixture and one
  negative case per limb
- Modify: `deploy/keycloak/README.md` — the new obligation, and the projection
  bullet that says the client scopes are not in the judged object
- Modify: `.github/secret-scan/allowed/deploy.txt` — the new local default
- Modify: `.github/secret-scan/allowed/tests.txt` — the same value's finding in
  `RealmImportTests`

**Gates this step turns red, and what turns them green:** ADR-052 marks
`RealmImportTests.No_client_ships_a_secret_but_the_one_whose_grant_needs_one`,
`RealmImportTests.The_permission_vocabulary_is_a_closed_set_of_client_roles` and
`RealmClientTests.It_is_the_only_service_account_client_in_the_realm` as
**asserted** — each goes red on the realm edit and green on the test edit in
this same task. The secret scan goes red on the two new literals and green on
the two allow-list entries, also here.

- [ ] **Step 1: Write the failing test edits**

In `RealmImportTests`, the closed set gains one name:

```csharp
        roles.ShouldBe(
            [
                "catalog:write",
                "inventory:admin",
                "payments:admin",
                "orders:write",
                "orders:cancel",
                "orders:admin",
                "orders:delivery-address"
            ],
            ignoreOrder: true);
```

The credentialed-client constants become a set, because there are two. **Each
value keeps a constant of its own with a credential-shaped name**, and that is
not a stylistic choice: `.github/secret-scan` reports
`DocumentedLocalSecret = "local-dev-secret"` as a `credential-assignment`, and
`.github/secret-scan/allowed/tests.txt` already carries the accepted finding
for it. Folding the value into a dictionary entry makes that finding vanish,
and an allow-list entry matching nothing fails the build (the README beside
the lists):

```csharp
    /// <summary>
    /// The clients whose grant requires both sides to agree on a secret
    /// (§11.5, ADR-052), and the documented local default each agrees on.
    /// </summary>
    /// <remarks>
    /// A client-credentials flow is two parties holding the same string, one
    /// of which is a committed file, so a generated secret would leave the
    /// realm and the deployment disagreeing. Pinning each value keeps the
    /// rule strong: a generated secret fails here, and so does a real one.
    /// </remarks>
    private const string CredentialClient = "web-bff";

    private const string DocumentedLocalSecret = "local-dev-secret";

    /// <summary>ADR-052's second credentialed client, and its own default.</summary>
    private const string WorkerCredentialClient = "shipping-worker";

    private const string DocumentedLocalWorkerSecret = "local-dev-shipping-secret";

    private static readonly Dictionary<string, string> DocumentedLocalSecrets =
        new(StringComparer.Ordinal)
        {
            [CredentialClient] = DocumentedLocalSecret,
            [WorkerCredentialClient] = DocumentedLocalWorkerSecret
        };

    [Fact]
    public void No_client_ships_a_secret_but_the_ones_whose_grants_need_one()
    {
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            string clientId = client.GetProperty("clientId").GetString()!;
            bool ships = client.TryGetProperty("secret", out JsonElement secret);

            if (!DocumentedLocalSecrets.TryGetValue(clientId, out string? documented))
            {
                // §11.6 and the local-development carve-out: Compose's
                // documented defaults are deliberate, and a randomly generated
                // secret is not one of them. Keycloak regenerates on import.
                ships.ShouldBeFalse($"'{clientId}' ships a secret and needs none");

                continue;
            }

            ships.ShouldBeTrue(
                $"'{clientId}' authenticates with the client-credentials grant, so the realm and " +
                "the deployment have to hold the same value (§11.5)");

            // The documented default and nothing else. The matching half lives
            // in the host's own Compose unit, which a building block's suite
            // may not read.
            secret.GetString().ShouldBe(
                documented,
                "a secret in a committed realm must be the documented local default, " +
                "never a generated or real one (§11.6)");
        }

        // Not vacuous: with a set that named a client the realm does not hold,
        // every branch above would take the first arm and assert nothing about
        // the credential this platform actually ships.
        string[] present =
        [
            .. Root.GetProperty("clients").EnumerateArray()
                .Select(c => c.GetProperty("clientId").GetString()!)
        ];

        foreach (string credentialed in DocumentedLocalSecrets.Keys)
            present.ShouldContain(credentialed);
    }
```

and a new assertion, which is the static half of the grant ADR-052 says each
client holds itself to:

```csharp
    [Fact]
    public void The_worker_service_account_holds_exactly_the_role_its_grant_names()
    {
        // The `permission` mapper is oidc-usermodel-client-role-mapper, so a
        // service account's claim comes from the client roles assigned to its
        // own user — which a realm export carries as a user with
        // serviceAccountClientId. Without that user the client authenticates
        // and its token carries no permission at all, which reads at the
        // reader as PermissionDenied and at the realm as nothing wrong.
        JsonElement account = Root.GetProperty("users").EnumerateArray()
            .Single(u => u.TryGetProperty("serviceAccountClientId", out JsonElement client) &&
                         client.GetString() == "shipping-worker");

        string[] granted =
        [
            .. account.GetProperty("clientRoles").GetProperty(Audience).EnumerateArray()
                .Select(r => r.GetString()).OfType<string>()
        ];

        // Exactly, not ShouldContain. ADR-052 sizes this credential by what it
        // reads when it is stolen, and a second role here is a second thing it
        // reads — which is the decision that record exists to hold.
        granted.ShouldBe(["orders:delivery-address"]);
    }
```

In `RealmClientTests`, the name is now false and moves with the assertion:

```csharp
    /// <summary>The second client ADR-052 mints, and the reader of Ordering's address.</summary>
    private const string WorkerClient = "shipping-worker";

    [Fact]
    public void The_service_account_clients_are_exactly_the_hosts_that_call_a_peer()
    {
        string[] serviceAccounts =
        [
            .. Realm.RootElement
                .GetProperty("clients")
                .EnumerateArray()
                .Where(c =>
                    c.TryGetProperty("serviceAccountsEnabled", out JsonElement enabled) &&
                    enabled.GetBoolean())
                .Select(c => c.GetProperty("clientId").GetString()!)
        ];

        // §11.5 makes the number of hosts holding a client secret the number of
        // synchronous couplings in the platform, and ADR-052 moved it from one
        // to two by deciding what the second one reads when it is stolen. Over-
        // supply has no failing test to catch it — which is what this is — so a
        // third name appearing here is a third coupling or a credential nothing
        // sends. Notifications' client is decided and not built.
        serviceAccounts.ShouldBe([ClientId, WorkerClient], ignoreOrder: true);
    }
```

Run both suites and see them fail:

```bash
dotnet test tests/Common.Web.Tests --filter "Category!=Integration"
dotnet test tests/Web.Bff.Tests --filter "Category!=Integration"
```

Expected: `The_permission_vocabulary_is_a_closed_set_of_client_roles` reports a
missing `orders:delivery-address`;
`No_client_ships_a_secret_but_the_ones_whose_grants_need_one` fails its new
non-vacuity loop on an absent `shipping-worker`;
`The_worker_service_account_holds_exactly_the_role_its_grant_names` throws from
`Single`; and
`The_service_account_clients_are_exactly_the_hosts_that_call_a_peer`
reports one name where two are expected.

- [ ] **Step 2: The realm**

In `roles.client.commerce-api`, after `orders:admin`, a new object in the
existing shape with a **fresh** `id` GUID and `containerId` copied from
`orders:admin`'s:

```jsonc
        {
          "id": "6b0f2d94-8a17-4e53-9c26-0d4a71e3b85f",
          "name": "orders:delivery-address",
          "description": "Read one order's delivery address over gRPC (ADR-052). Held by the shipping-worker service account and by no person: the read crosses subjects, so the method skips the ownership check and an authenticated caller alone would be too many.",
          "composite": false,
          "clientRole": true,
          "containerId": "e55a8854-8088-495c-8db4-331d4b5eb68e",
          "attributes": {}
        }
```

The description stays inside Keycloak's `ROLE.DESCRIPTION` column, whose bound
is `RealmImportTests.No_role_description_exceeds_what_keycloak_can_store`'s
`keycloakDescriptionLimit` and is written down nowhere else; an over-long value
throws on import rather than truncating, so run that test rather than count
characters against a number copied into this plan.

In `clients`, after `web-bff` and before `mobile-app`:

```jsonc
    {
      "id": "7c5a1d38-9e02-4b6f-8134-2fa6c0d75e91",
      "clientId": "shipping-worker",
      "name": "Shipping worker",
      "description": "The second host that calls a service synchronously (ADR-052): a Shipping worker reading one order's delivery address. Service accounts only, holding orders:delivery-address and nothing else.",
      "surrogateAuthRequired": false,
      "enabled": true,
      "alwaysDisplayInConsole": false,
      "clientAuthenticatorType": "client-secret",
      "secret": "local-dev-shipping-secret",
      "redirectUris": [],
      "webOrigins": [],
      "notBefore": 0,
      "bearerOnly": false,
      "consentRequired": false,
      "standardFlowEnabled": false,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": true,
      "publicClient": false,
      "frontchannelLogout": false,
      "protocol": "openid-connect",
      "attributes": {
        "realm_client": "false",
        "backchannel.logout.session.required": "true",
        "backchannel.logout.revoke.offline.tokens": "false"
      },
      "authenticationFlowBindingOverrides": {},
      "fullScopeAllowed": true,
      "nodeReRegistrationTimeout": -1,
      "defaultClientScopes": [
        "web-origins",
        "acr",
        "profile",
        "roles",
        "basic",
        "email",
        "commerce-api"
      ],
      "optionalClientScopes": [
        "address",
        "phone",
        "organization",
        "offline_access",
        "microprofile-jwt"
      ],
      "access": {
        "view": true,
        "configure": true,
        "manage": true
      }
    },
```

And `web-bff`'s own `description` stops saying it is alone — ADR-052's table
names this file for exactly that, and the client this step adds is what makes
the sentence false. It reads today:

> The one host that calls a service synchronously (§9.7), and therefore the
> only one holding client credentials (§11.5). Service accounts only: no
> browser flow, and nothing can obtain a token as a person through it.

and becomes:

> One of the two hosts that call a service synchronously and hold client
> credentials (§9.7, §11.5, ADR-052). Service accounts only: no browser flow,
> and nothing can obtain a token as a person through it.

The realm's `internationalizationEnabled` is the other half of ADR-052's row
for this file and is **not** touched here: no user has a locale to read, that
is Notifications' question, and this pull request mints no reader of one.

`commerce-api` is a **default** scope and appears in no optional list: a
client-credentials token requests no scope explicitly, so an optional one is
silently absent and the token carries neither the audience nor the `permission`
claim (§11.5).

In `users`, after `browser`:

```jsonc
    {
      "username": "service-account-shipping-worker",
      "enabled": true,
      "serviceAccountClientId": "shipping-worker",
      "clientRoles": {
        "commerce-api": [
          "orders:delivery-address"
        ]
      }
    }
```

The `permission` mapper is Keycloak's *User Client Role* mapper, so this user
is the only thing that puts the role into the client's token. No password and
no email: a service account's user is not a login.

```bash
dotnet test tests/Common.Web.Tests --filter "Category!=Integration"
dotnet test tests/Web.Bff.Tests --filter "Category!=Integration"
```

Expected: green.

- [ ] **Step 3: The realm gate's predicate**

`realm_check.py` reads the realm and its client list, and ADR-052 is explicit
that a service account's roles are in **neither** document — reading them
would
widen this gate's credential past the `view-clients`-only grant
`docs/secrets.md` argues for. So the predicate is not about the grant. It is
about everything else a stolen secret's blast radius depends on, all of which
the client object states.

Beside `MOBILE_CLIENT`:

```python
# The address reader ADR-052 mints, named for the same reason the two above
# are: its obligations are properties of one client and cannot be checked
# without finding it. Notifications' client is decided and unbuilt, so it is
# deliberately not here — a gate that required a client nobody deploys would
# fail every realm.
WORKER_CLIENT = "shipping-worker"
```

`CLIENT_FIELDS` gains the three keys the new check reads, because `judged`
projects the document down to exactly what the checks read and a field absent
from that tuple is a check that always sees `None`:

```python
CLIENT_FIELDS = ("clientId", "enabled", "standardFlowEnabled", "implicitFlowEnabled",
                 "directAccessGrantsEnabled", "serviceAccountsEnabled", "publicClient",
                 "redirectUris", "defaultClientScopes", "optionalClientScopes", "webOrigins")
```

and `FLAGS` gains the two it compares by identity, so a hand-edited `"true"` is
refused rather than read as off:

```python
FLAGS = (
    "implicitFlowEnabled",
    "standardFlowEnabled",
    "directAccessGrantsEnabled",
    "serviceAccountsEnabled",
    "enabled",
)
```

In `check_realm`, beside the browser and mobile lookups:

```python
    worker = [c for c in clients if isinstance(c, dict) and c.get("clientId") == WORKER_CLIENT]
    if len(worker) != 1:
        problems.append(
            f"the realm declares the address reader {WORKER_CLIENT!r} "
            f"{len(worker)} time(s), expected exactly one. ADR-052 sizes that "
            "client by what it reads when its secret is stolen, and every "
            "obligation below is a property of the client object")
```

and, after the mobile block:

```python
    if worker:
        problems += check_worker_client(worker[0])
```

The check itself, after `check_mobile_client`:

```python
def check_worker_client(client: dict) -> list[str]:
    """ADR-052's ceiling on the address reader, as far as a realm document reaches.

    The grant itself is out of reach and that record says so: a service
    account's roles live on its user, which is in neither the realm
    representation this gate is handed nor the client list `read_admin.py`
    fetches. What is left here is the rest of a stolen secret's blast radius —
    that the client is confidential, mints tokens for itself alone, and carries
    the scope whose mapper writes the `permission` claim at all.
    """
    problems: list[str] = []

    if client.get("enabled") is not True:
        problems.append(
            f"client {WORKER_CLIENT!r} is disabled. Every obligation below "
            "then holds because the client mints nothing, and the address read "
            "fails as a refused credential in whichever environment imported "
            "this realm")

    if client.get("publicClient") is not False:
        problems.append(
            f"client {WORKER_CLIENT!r} has publicClient="
            f"{client.get('publicClient')!r}. A public client presents no "
            "secret, so the grant ADR-052 gives this reader is one Keycloak "
            "refuses outright")

    if client.get("serviceAccountsEnabled") is not True:
        problems.append(
            f"client {WORKER_CLIENT!r} has service accounts disabled. Keycloak "
            "refuses the client-credentials grant with unauthorized_client, "
            "which reaches the worker as a refused credential — ADR-052's "
            "fourth row, a defect somebody must see rather than an outage")

    for flag, what in (("standardFlowEnabled", "an authorization-code flow"),
                       ("directAccessGrantsEnabled", "a password grant"),
                       ("implicitFlowEnabled", "an implicit flow")):
        if client.get(flag) is not False:
            problems.append(
                f"client {WORKER_CLIENT!r} has {flag}={client.get(flag)!r}, "
                f"which gives it {what}. Its secret is a deployment value, so "
                "the blast radius of that secret leaking has to stay one order's "
                "address and never a token for a person in this realm (ADR-052)")

    defaults = client.get("defaultClientScopes")
    defaults = defaults if isinstance(defaults, list) else []
    optional = client.get("optionalClientScopes")
    optional = optional if isinstance(optional, list) else []

    if "commerce-api" not in defaults:
        problems.append(
            f"client {WORKER_CLIENT!r} does not hold commerce-api as a default "
            "client scope. A client-credentials token requests no scope by "
            "name, so the audience mapper never runs and the permission claim "
            "is never written — the read is refused with nothing in this "
            "file's other checks to say why (§11.5)")

    if "commerce-api" in optional:
        problems.append(
            f"client {WORKER_CLIENT!r} also holds commerce-api as an OPTIONAL "
            "scope. Keycloak's admin console will create that state and it "
            "resolves in the wrong direction for a grant that names no scope")

    return problems
```

`main`'s closing line needs no edit: it counts clients and reports the
lifetime, and neither number is a claim about this check.

- [ ] **Step 4: The README that owns the gate's claim**

`deploy/keycloak/README.md` is where this gate's claim lives —
`docs/change-locality.md` gives a gate's README that job and forbids the claim
being stated anywhere else — so a new predicate that is not in it is a check
nobody can find. Two edits, and the second is the one easy to miss.

Under *What it asserts*, after the `mobile-app` bullet:

```markdown
- **`shipping-worker`'s own shape**, as far as a client object reaches: one
  such client, confidential, service accounts on, no interactive flow, and
  `commerce-api` a default client scope and not an optional one — cited rather
  than enumerated here, because
  [ADR-052](../../docs/backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
  argues each of them and `check_worker_client` is the list.
```

Under *What it does not check*, the bullet beginning "**Everything else in the
realm**" says the client scopes are not in the judged object at all. Step 3
put `optionalClientScopes` into `CLIENT_FIELDS` beside the `defaultClientScopes`
already there, so that clause is corrected in place rather than appended to:
both scope **lists** are in the projection, for the one obligation above that
reads them, while the scopes' own definitions, their mappers and the audience
mapper still are not. The rest of the bullet — the permission vocabulary, the
two development logins, every client secret, and why no message here can leak
a credential — is unchanged.

Under *What it asserts*, the grant itself stays out: say once, and only here,
that `check_worker_client` reaches the client object and not the service
account's roles, and that the token client's own check is the other half
(ADR-052).

- [ ] **Step 5: The gate's own tests**

In `test_realm_check.py`, a fixture beside `browser()` and `mobile()`:

```python
def worker(**overrides) -> dict:
    """A compliant `shipping-worker`: confidential, service accounts on, no
    interactive flow, and commerce-api as a default scope and not an optional
    one."""
    client = {
        "clientId": realm_check.WORKER_CLIENT,
        "enabled": True,
        "standardFlowEnabled": False,
        "implicitFlowEnabled": False,
        "directAccessGrantsEnabled": False,
        "serviceAccountsEnabled": True,
        "publicClient": False,
        "defaultClientScopes": [
            "web-origins", "acr", "profile", "roles", "basic", "email", "commerce-api"],
        "optionalClientScopes": ["address", "phone", "organization"],
        "webOrigins": [],
    }
    client.update(overrides)
    return client
```

`realm()` appends it on the same terms it already appends `mobile()`, so a
case that passes its clients positionally learns nothing:

```python
    if clients:
        client_list = list(clients)
        if not any(isinstance(c, dict) and c.get("clientId") == realm_check.MOBILE_CLIENT
                   for c in client_list):
            client_list.append(mobile())
        if not any(isinstance(c, dict) and c.get("clientId") == realm_check.WORKER_CLIENT
                   for c in client_list):
            client_list.append(worker())
    else:
        client_list = [browser(), mobile(), worker()]
```

Two cases pass `clients=` as a keyword instead, and `realm()` applies its
overrides after the list is built, so neither gets the worker appended.
`test_a_realm_missing_the_mobile_client_is_refused` counts its problems and
asserts exactly one, the mobile client's absence, and after Step 3 it would
see two. Amend it to `realm(clients=[browser(), other, worker()])`: its
subject is the mobile client, so the worker belongs in its document. The
other, `test_rotation_is_not_checked_without_a_mobile_client`, asserts with
`any(...)` over the messages rather than a count, and stands as it is.

`WhatTheGateHolds` does not go through `realm()`: its `realm_with_secrets`
writes a two-client document of its own, because every field in it is there to
be looked for in the output. Step 3 makes a third client mandatory, and
`test_the_fields_every_check_reads_do_survive` asserts the whole verdict —
`check_realm(held, realm_check.DEPLOYED, 300) == []` — so that document gains
one too. The fixture, not a fourth copy of the shape, and it carries nothing
credential-shaped for the projection to drop:

```python
            }, worker()],
        }
```

Neither case in that class has to learn the client either: the redaction one
iterates `MARKERS`, which this client adds none of, and the projection one
reads `held["clients"][0]`, which is still the browser client. What it does
add is that the projection has to keep `serviceAccountsEnabled`, `enabled` and
`optionalClientScopes` — exactly the fields Step 3 put into `CLIENT_FIELDS`.

Then the new cases:

```python
class TheWorkerClient(Fixture):
    def test_a_missing_worker_is_caught_rather_than_passed(self):
        """The vacuous half: every check below is a property of one client."""
        document = realm(browser(), mobile())
        document["clients"] = [c for c in document["clients"]
                               if c.get("clientId") != realm_check.WORKER_CLIENT]
        self.assertIn("shipping-worker", self.one(document))

    def test_a_public_worker_is_caught(self):
        self.assertIn("publicClient", self.one(realm(browser(), worker(publicClient=True))))

    def test_service_accounts_turned_off_is_caught(self):
        self.assertIn("service accounts disabled",
                      self.one(realm(browser(), worker(serviceAccountsEnabled=False))))

    def test_a_disabled_worker_is_caught(self):
        self.assertIn("disabled", self.one(realm(browser(), worker(enabled=False))))

    def test_each_interactive_flow_is_caught_on_its_own(self):
        for flag in ("standardFlowEnabled", "directAccessGrantsEnabled", "implicitFlowEnabled"):
            with self.subTest(flag=flag):
                found = self.problems(realm(browser(), worker(**{flag: True})))
                # implicitFlowEnabled is also caught by check_implicit_flow,
                # which judges every client — two findings there, one for the
                # others, and both name the flag.
                self.assertTrue(any(flag in problem for problem in found), found)

    def test_an_optional_audience_scope_is_caught(self):
        """The default list keeps commerce-api, so only the optional limb fires."""
        found = self.one(realm(browser(), worker(
            optionalClientScopes=["address", "commerce-api"])))
        self.assertIn("OPTIONAL", found)

    def test_a_missing_audience_scope_is_caught(self):
        self.assertIn("default client scope",
                      self.one(realm(browser(), worker(defaultClientScopes=["basic"]))))

    def test_a_string_service_account_flag_is_refused_rather_than_read_as_on(self):
        """The flag joined FLAGS, so a hand-edited "true" is refused.

        Two findings and not one, so `self.problems` rather than `self.one`:
        `check_flags_are_booleans` refuses the string, and the identity test
        in `check_worker_client` sees a value that is not `True`. Both are
        asserted, because a case naming one would stay green if the other
        limb were deleted.
        """
        found = self.problems(realm(browser(), worker(serviceAccountsEnabled="true")))
        self.assertTrue(any("boolean" in problem for problem in found), found)
        self.assertTrue(any("service accounts disabled" in problem for problem in found), found)
```

`Fixture.one` asserts exactly one problem, so a case that trips two limbs has
to say so. Two do: the string flag above, which `check_flags_are_booleans`
and `check_worker_client` both see; and an audience scope held as optional
*and* missing from the defaults, which is why the optional case leaves the
default list alone and the missing-scope case leaves the optional list alone.
Each then names one limb, and neither can pass for the other's reason.

```bash
py -3.12 -m unittest discover -s deploy/keycloak
py -3.12 deploy/keycloak/realm_check.py check --kind local --realm deploy/compose/keycloak/realm-export.json
```

Suite first, then the gate against the realm this task just edited. Expected:
both exit 0.

- [ ] **Step 6: The secret scan**

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: two new findings, rule `credential-assignment` — one on
`deploy/compose/keycloak/realm-export.json` and one on
`tests/Common.Web.Tests/RealmImportTests.cs`, both for
`local-dev-shipping-secret`. Take each fingerprint **from the scan's own
output** — it is a sha256 over the finding and cannot be written in advance —
and add one line to `.github/secret-scan/allowed/deploy.txt`, beside the
existing realm-export entries:

```
deploy/compose/keycloak/realm-export.json | credential-assignment | <fingerprint> | ADR-052's second client, and §14.1's local default for it.
```

and one to `.github/secret-scan/allowed/tests.txt`, beside the other
`RealmImportTests.cs` entries:

```
tests/Common.Web.Tests/RealmImportTests.cs | credential-assignment | <fingerprint> | The suite pinning the realm export's second documented local client default.
```

The entry that is already there for `local-dev-secret` still matches, because
Step 1 keeps `DocumentedLocalSecret` a constant with its own name and value;
an entry that stopped matching would fail the build on the entry rather than
on the code.

Re-run the scan; expected: clean, and no unmatched allow-list entry.

- [ ] **Step 7: Commit**

```bash
dotnet test tests/Common.Web.Tests --filter "Category!=Integration"
dotnet test tests/Web.Bff.Tests --filter "Category!=Integration"
git add deploy/compose/keycloak/realm-export.json deploy/keycloak \
        .github/secret-scan/allowed/deploy.txt .github/secret-scan/allowed/tests.txt \
        tests/Common.Web.Tests/RealmImportTests.cs tests/Web.Bff.Tests/RealmClientTests.cs
git commit -m "feat(ordering): the realm gains orders:delivery-address and the shipping-worker client"
```

---

### Task 4: The permission, the policy and `DeliveryAddressService`

**Files:**
- Modify: `src/Services/Ordering/Ordering.Api/OrderingPermissions.cs` — the
  third constant, and the remark that says there are two
- Modify: `src/Services/Ordering/Ordering.Api/Program.cs` — the policy and
  `MapGrpcService`
- Create: `src/Services/Ordering/Ordering.Api/Grpc/DeliveryAddressService.cs`
- Test: `tests/Ordering.Api.Tests/DeliveryAddressServiceTests.cs`
- Modify: `tests/Ordering.Api.Tests/AuthorizationPolicyTests.cs` — the
  non-vacuity list

**Interfaces:**
- Consumes: `GetDeliveryAddressQuery`, `DeliveryAddressView` (Task 2);
  `DeliveryAddresses.DeliveryAddressesBase`, `GetDeliveryAddressRequest`,
  `GetDeliveryAddressReply` (Task 1); `IDispatcher.QueryAsync`.
- Produces: `OrderingPermissions.DeliveryAddress = "orders:delivery-address"`.

- [ ] **Step 1: Write the failing tests**

`tests/Ordering.Api.Tests/DeliveryAddressServiceTests.cs`:

```csharp
using Grpc.Core;
using Grpc.Net.Client;
using Ordering.Api;
using Ordering.Delivery.V1;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// ADR-052's method over the real pipeline: authentication, the permission
/// policy, the dispatcher and Dapper on a real database.
/// </summary>
/// <remarks>
/// Over <c>TestServer</c>: <c>CreateHandler()</c> bypasses the network, so
/// the h2c negotiation a real Kestrel would need never happens here.
/// Whether the endpoint is declared <c>Http2</c> is the server's decision
/// and belongs against a real Kestrel (§9.7).
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class DeliveryAddressServiceTests(ServiceFixture fixture) : IAsyncLifetime
{
    private GrpcChannel _channel = null!;

    public async ValueTask InitializeAsync()
    {
        _channel = GrpcChannel.ForAddress(
            fixture.Factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = fixture.Factory.Server.CreateHandler() });

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();

        return ValueTask.CompletedTask;
    }

    private DeliveryAddresses.DeliveryAddressesClient Addresses => new(_channel);

    /// <summary>
    /// The principal a validated <c>shipping-worker</c> token becomes (§11.3),
    /// as call metadata.
    /// </summary>
    /// <remarks>
    /// Passed per call rather than baked into the channel: a default grant is
    /// how a suite ends up proving a policy is applied while never once
    /// arriving without it.
    /// </remarks>
    private static Metadata Worker() =>
    [
        new Metadata.Entry(TestAuthHandler.UserHeader, "service-account-shipping-worker"),
        new Metadata.Entry(TestAuthHandler.PermissionsHeader, OrderingPermissions.DeliveryAddress)
    ];

    /// <summary>
    /// A person holding every permission this service's vocabulary can grant a
    /// person, including the admin claim that overrides the ownership check.
    /// </summary>
    /// <remarks>
    /// <c>orders:admin</c> is spelt as a literal because §11.4 keeps it out of
    /// <c>OrderingPermissions</c>: it is a claim <c>CancelOrderHandler</c>
    /// reads and not a policy an endpoint names.
    /// </remarks>
    private static Metadata EveryUserPermission() =>
    [
        new Metadata.Entry(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString()),
        new Metadata.Entry(
            TestAuthHandler.PermissionsHeader,
            $"{OrderingPermissions.Write} {OrderingPermissions.Cancel} orders:admin")
    ];

    private static GetDeliveryAddressRequest For(Guid orderId) => new() { OrderId = orderId.ToString() };

    private static async Task<StatusCode> StatusOfAsync(Func<Task<GetDeliveryAddressReply>> call)
    {
        RpcException thrown = await Should.ThrowAsync<RpcException>(call);

        return thrown.StatusCode;
    }

    [Fact]
    public async Task A_caller_with_no_token_is_Unauthenticated()
    {
        // No principal: the channel with no metadata. Without this the whole
        // credential mechanism ADR-052 mints could be missing and every other
        // test here would still be green.
        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(Guid.CreateVersion7()), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task A_person_holding_every_user_permission_is_PermissionDenied()
    {
        // The half the first test cannot make: a host that stopped routing
        // answers Unauthenticated to everything, so "no token is refused" is
        // satisfiable by a service that is not there. This one says the
        // permission is doing the work — orders:delivery-address belongs to a
        // host and to no person (ADR-052), so even the admin claim is refused.
        Guid order = await fixture.SeedOrderAsync(Guid.CreateVersion7());

        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(order), EveryUserPermission(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task The_client_reads_the_address_and_the_customer_and_nothing_else()
    {
        Guid customer = Guid.CreateVersion7();
        Guid order = await fixture.SeedOrderAsync(customer);

        GetDeliveryAddressReply reply = await Addresses.GetAsync(
            For(order),
            Worker(),
            cancellationToken: TestContext.Current.CancellationToken);

        // SeedOrderAsync's address, field for field.
        reply.CustomerId.ShouldBe(customer.ToString());
        reply.Line1.ShouldBe("1 Test Street");
        reply.Line2.ShouldBe("", "proto3 has no null string, so an absent second line is empty");
        reply.City.ShouldBe("Almaty");
        reply.PostCode.ShouldBe("050000");
        reply.Country.ShouldBe("KZ");

        // Nothing of the order travels. A reply that grew a status or a total
        // would make the reader able to decide things ADR-052 keeps here.
        reply.ToString().ShouldNotContain("AwaitingStock");
        reply.ToString().ShouldNotContain("19.99");
    }

    [Fact]
    public async Task An_order_that_does_not_exist_is_NotFound()
    {
        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(
                    For(Guid.CreateVersion7()),
                    Worker(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task A_cancelled_order_is_NotFound_rather_than_an_address()
    {
        // ADR-052's "does not exist is wider than a missing record": all three
        // cases answer NotFound so the client maps a status and never reads an
        // order's state.
        Guid order = await fixture.SeedOrderAsync(Guid.CreateVersion7());
        await fixture.ExecuteAsync(
            "UPDATE ordering.Orders SET Status = 'Cancelled' WHERE Id = {0};", order);

        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(order), Worker(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task An_order_whose_address_erasure_has_cleared_is_NotFound()
    {
        // §11.7's extension is owed and this is what it will produce: the
        // order's own record whole and its address gone. Written as raw SQL
        // because no consumer produces it yet, which is exactly ADR-052's
        // instruction to design against it rather than meet it later.
        Guid order = await fixture.SeedOrderAsync(Guid.CreateVersion7());
        await fixture.ExecuteAsync(
            """
            UPDATE ordering.Orders
            SET ShipToLine1 = '', ShipToLine2 = NULL, ShipToCity = '', ShipToPostalCode = '', ShipToCountry = ''
            WHERE Id = {0};
            """,
            order);

        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(order), Worker(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task A_malformed_order_id_is_InvalidArgument()
    {
        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Addresses
                .GetAsync(
                    new GetDeliveryAddressRequest { OrderId = "not-a-guid" },
                    Worker(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        // Untranslated this is Unknown, which rides grpc-status on an HTTP 200
        // and would reach the worker as neither an answer nor a transient
        // fault — so the shipment would back off for ever on a request that
        // can never succeed.
        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);

        // The field, never the value: it is a caller-supplied string arriving
        // in a message that reaches the logs, and §13.4's redactor cannot see a
        // value interpolated into one.
        thrown.Status.Detail.ShouldContain("order_id");
        thrown.Status.Detail.ShouldNotContain("not-a-guid");
    }
}
```

In `AuthorizationPolicyTests.Every_policy_an_endpoint_names_resolves`, the
non-vacuity list gains the third name — the gRPC endpoint carries the service
class's `[Authorize]` metadata, so it is read off `EndpointDataSource` with the
other two:

```csharp
        named.ShouldContain(OrderingPermissions.Write);
        named.ShouldContain(OrderingPermissions.Cancel);
        named.ShouldContain(
            OrderingPermissions.DeliveryAddress,
            "ADR-052's method is the first in this service behind a policy no endpoint route names");
```

- [ ] **Step 2: Run to see them fail**

```bash
dotnet test tests/Ordering.Api.Tests --filter "FullyQualifiedName~DeliveryAddressServiceTests"
```

Expected: compile failure on `OrderingPermissions.DeliveryAddress` alone —
the `Ordering.Delivery.V1.DeliveryAddresses` types exist from Task 1's proto,
and a service nobody maps is not a compile error. Then, once the constant
exists but no `MapGrpcService` names the service, `Unimplemented` on every
call.

- [ ] **Step 3: Write the permission, the service and the policy**

`OrderingPermissions.cs` gains the constant, and the doc comment that counts
two entries is **replaced whole** rather than appended to. The comment gate
judges a block by its whole run once any line in it is touched, and that block
runs well past ten lines today, so a `<para>` added to it is a finding on
everything above it as well. What the shorter block loses is argument its
owners already carry — §11.4 for the claim-against-policy distinction, and
`AuthorizationPolicyTests` for the not-registered-at-all half — which is the
form the style guide asks for anyway:

```csharp
/// <summary>
/// Ordering's permission vocabulary (§11.4): the strings are the contract with
/// the realm's claim mapper (§11.5), and <c>Program.cs</c> registers a policy
/// per name. <c>orders:delivery-address</c> guards
/// <see cref="Grpc.DeliveryAddressService"/>, ADR-052's gRPC method; it
/// belongs to a host and to no person, because the read crosses subjects and
/// the method skips the ownership check. <c>orders:read</c> is absent until a
/// read endpoint requires it, and <c>orders:admin</c> is a claim
/// <c>CancelOrderHandler</c> checks against a loaded aggregate (§11.4).
/// </summary>
```

```csharp
    public const string Write = "orders:write";
    public const string Cancel = "orders:cancel";
    public const string DeliveryAddress = "orders:delivery-address";
```

`Grpc/DeliveryAddressService.cs`:

```csharp
using Common.Application;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Ordering.Application.Orders.GetDeliveryAddress;
using Ordering.Delivery.V1;

namespace Ordering.Api.Grpc;

/// <summary>
/// The server half of ADR-052's address read: parse, dispatch, project onto
/// the reply — the job <c>OrderEndpoints</c> does for HTTP, under §4.2's gate.
/// </summary>
/// <remarks>
/// A permission where Catalog's gRPC service asks only for authentication,
/// because this answers with somebody's address and an authenticated caller
/// alone is every client the realm holds. No ownership check either: a
/// service account's subject owns no order, so the grant bounds it (§11.4).
/// </remarks>
[Authorize(OrderingPermissions.DeliveryAddress)]
internal sealed class DeliveryAddressService(IDispatcher dispatcher) : DeliveryAddresses.DeliveryAddressesBase
{
    public override async Task<GetDeliveryAddressReply> Get(
        GetDeliveryAddressRequest request,
        ServerCallContext context)
    {
        // TryParseExact with "D", not TryParse: the contract says a GUID in its
        // canonical text form, and TryParse also accepts the N, B and P
        // formats. Accepting more than the contract states is how two ends stop
        // agreeing about what the contract is.
        if (!Guid.TryParseExact(request.OrderId, "D", out Guid orderId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "order_id is not a GUID."));

        DeliveryAddressView? address = await dispatcher.QueryAsync(
            new GetDeliveryAddressQuery(orderId),
            context.CancellationToken);

        // One status for three facts (ADR-052): no such order, a cancelled
        // one, and one whose address erasure has cleared. The detail says no
        // more than the status, so a caller cannot recover the distinction the
        // handler deliberately collapsed.
        if (address is null)
            throw new RpcException(new Status(StatusCode.NotFound, "No delivery address for that order."));

        return new GetDeliveryAddressReply
        {
            CustomerId = address.CustomerId.ToString(),
            Line1 = address.Line1,
            // proto3 has no null string; the absence of a second line is "".
            Line2 = address.Line2 ?? string.Empty,
            City = address.City,
            PostCode = address.PostalCode,
            Country = address.Country
        };
    }
}
```

In `Program.cs`, beside `AddOpenApi`:

```csharp
// ADR-052's server half. No interceptor: this service has no validator on the
// query, so nothing throws a ValidationException for one to translate — the
// only caller-supplied value is parsed above the dispatcher and refused there.
builder.Services.AddGrpc();
```

the policy joins the builder:

```csharp
    .AddPolicy(OrderingPermissions.Cancel, p => p.RequirePermission(OrderingPermissions.Cancel))
    .AddPolicy(OrderingPermissions.DeliveryAddress, p => p.RequirePermission(OrderingPermissions.DeliveryAddress));
```

and the service is mapped after the endpoints:

```csharp
// ADR-052. Reachable only on the Http2 endpoint appsettings.json declares —
// gRPC needs HTTP/2, and mapping it says nothing about which port serves it.
// The [Authorize] is on the service class, not here, so it travels with the
// type rather than with this line.
app.MapGrpcService<DeliveryAddressService>();
```

with `using Ordering.Api.Grpc;` at the top of the file.

The comment block above that builder counts two policies and is corrected in
place. It has to come out at ten lines or fewer whole, because the gate judges
the run and not the sentence:

```csharp
// RequirePermission rather than RequireClaim("permission", …): the claim type
// is PermissionClaim.Type, and spelling the literal here would be a fourth
// place that has to agree with it (§11.4).
//
// One policy per name in OrderingPermissions; ADR-052's is a gRPC method's and
// no endpoint route names it. There is deliberately no orders:admin policy —
// that string is a *claim*, read by CancelOrderHandler against a loaded
// aggregate, and §11.4 is emphatic that a policy nobody registered resolves
// to nothing.
```

- [ ] **Step 4: Run; commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Ordering.Api.Tests
```

Expected: 0 warnings, green — including `GrantablePermissionTests`, which
reflects over `OrderingPermissions` and finds the role Task 3 put in the realm.

```bash
git add src/Services/Ordering tests/Ordering.Api.Tests
git commit -m "feat(ordering): DeliveryAddresses.Get under orders:delivery-address"
```

---

### Task 5: No route reaches the method

ADR-052: "no route in the gateway's file matches that path and no cluster dials
that port, and a gateway test says so." Catalog has had the first property
since PR-19 and nothing asserted it; this is the assertion, written over both
servers so it cannot pass for one and be silent about the other.

**Files:**
- Create: `tests/Gateway.Api.Tests/GrpcPathTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// The edge reaches no gRPC method, from both ends: no route matches a method
/// path, and no cluster dials a gRPC port (§9.7, ADR-052).
/// </summary>
/// <remarks>
/// §10.2 routes HTTP and the two gRPC surfaces are cluster-internal, on the
/// same footing as the SQL port. A route added under a path like these would
/// publish an authenticated-only internal call to the internet with nothing
/// else in this repository to say so.
/// </remarks>
public sealed class GrpcPathTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    /// <summary>
    /// Every gRPC method the platform serves, spelt as
    /// <c>/&lt;package&gt;.&lt;service&gt;/&lt;method&gt;</c>.
    /// </summary>
    /// <remarks>
    /// By hand, one line per method, on <c>ServiceGroups</c>' terms: reading
    /// them from the services would mean this project referencing every host,
    /// which is the coupling §10.1 exists to prevent in test clothing.
    /// </remarks>
    private static readonly string[] MethodPaths =
    [
        "/catalog.pricing.v1.Pricing/GetPrices",
        "/ordering.delivery.v1.DeliveryAddresses/Get"
    ];

    /// <summary>The ports the two gRPC endpoints bind (§9.7, ADR-052).</summary>
    private const string GrpcPort = ":8081";

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task No_route_matches_a_grpc_method_path(string path)
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            path,
            new ByteArrayContent([]),
            TestContext.Current.CancellationToken);

        // 404 and specifically not 401: an unmatched path has no endpoint, so
        // no policy runs on it. A 401 here would mean a route DID match and
        // the caller was merely unauthenticated, which is a published path
        // behind a token rather than no path at all.
        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            $"'{path}' is a gRPC method and §10.2 routes HTTP (ADR-052)");
    }

    public static TheoryData<string> Methods()
    {
        TheoryData<string> data = [];

        foreach (string path in MethodPaths)
            data.Add(path);

        return data;
    }

    [Fact]
    public void No_cluster_dials_a_grpc_port()
    {
        IConfiguration configuration = factory.Services.GetRequiredService<IConfiguration>();

        (string Cluster, string Address)[] destinations =
        [
            .. configuration.GetSection("ReverseProxy:Clusters").GetChildren()
                .SelectMany(cluster => cluster.GetSection("Destinations").GetChildren()
                    .Select(destination => (Cluster: cluster.Key, Address: destination["Address"] ?? string.Empty)))
        ];

        // The guard the loop below rests on: over an empty set it passes and
        // says nothing, which is what a renamed configuration section produces.
        destinations.ShouldNotBeEmpty();

        foreach ((string cluster, string address) in destinations)
        {
            address.ShouldNotContain(
                GrpcPort,
                Case.Sensitive,
                $"cluster '{cluster}' dials a gRPC port; the proxy speaks HTTP/1.1 to its destinations " +
                "and an Http2-only endpoint answers that with a 400 (§9.7)");
        }
    }
}
```

- [ ] **Step 2: Run**

```bash
dotnet test tests/Gateway.Api.Tests --filter "FullyQualifiedName~GrpcPathTests"
```

Expected: **green on the first run**, and that is the point rather than a
problem — this is a property the route file already has and nothing asserted.
Prove it by mutation rather than by the run: add a temporary route matching
`/{**catch-all}` to `src/Gateway/Gateway.Api/appsettings.json`, see
`No_route_matches_a_grpc_method_path` fail on both paths, and revert it. Do the
same for the second test by pointing the `catalog` cluster at
`http://catalog-api:8081/`. A test that has never once failed is a test whose
subject is unproven.

- [ ] **Step 3: Commit**

```bash
git add tests/Gateway.Api.Tests/GrpcPathTests.cs
git commit -m "test(gateway): no route matches a gRPC method path and no cluster dials one"
```

---

### Task 6: The client, proved both ways against a token Keycloak issued

ADR-052: "each client is proved both ways against a token Keycloak issued:
accepted with the grant, refused without." The realm assertions of Task 3 are
static; this is the live half, and it is the only place the `permission`
mapper, the default scope and the service-account role assignment are exercised
together.

**Files:**
- Modify: `tests/Web.Bff.Tests/KeycloakFixture.cs` — the worker's client id
  and
  secret as constants beside the realm's
- Modify: `tests/Web.Bff.Tests/KeycloakIdentityTests.cs` — two tests, a
  rename, a corrected comment, the class doc rewritten whole and a second
  route on the minimal host
- Modify: `.github/secret-scan/allowed/tests.txt` — the fixture's new
  credential-shaped constant

- [ ] **Step 1: Write the failing tests**

In `KeycloakFixture`, beside `Realm`:

```csharp
    /// <summary>
    /// ADR-052's second credentialed client and the documented local default
    /// it authenticates with (§14.1). Here rather than in each test class for
    /// the reason the admin pair above is: the realm file and this fixture are
    /// the two halves that have to agree.
    /// </summary>
    public const string WorkerClient = "shipping-worker";
    public const string WorkerSecret = "local-dev-shipping-secret";
```

In `KeycloakIdentityTests`, the minimal host gains the policy and a second
route. `ServiceValidatingTheRealm` becomes:

```csharp
    /// <summary>
    /// The permission ADR-052 gives the address reader, spelt as a literal.
    /// </summary>
    /// <remarks>
    /// <c>OrderingPermissions.DeliveryAddress</c> is the owner and this suite
    /// may not reference Ordering to read it; the realm's closed role set is
    /// what ties the two spellings together (§11.4, §11.5).
    /// </remarks>
    private const string DeliveryAddress = "orders:delivery-address";

    private async Task<WebApplication> ServiceValidatingTheRealm()
    {
        // Development, because the container speaks plain HTTP:
        // AddJwtAuthentication refuses a non-https authority outside
        // Development and RequireHttpsMetadata would stop the discovery
        // document being fetched at all (§11.3). Both are the same rule, and
        // the container is the case they carve out.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = keycloak.Authority;

        // The platform's own registration, not a copy of it. A hand-rolled
        // AddJwtBearer here would validate whatever this file decided to
        // validate and prove nothing about what a service does.
        builder.AddJwtAuthentication();

        // RequirePermission, not RequireClaim: the claim type is Common.Web's
        // (§11.4), so this policy and the one Ordering registers cannot drift
        // apart about where a permission lives in a token.
        builder.Services
            .AddAuthorizationBuilder()
            .AddPolicy(DeliveryAddress, p => p.RequirePermission(DeliveryAddress));

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", () => Results.Ok()).RequireAuthorization();
        app.MapGet("/address", () => Results.Ok()).RequireAuthorization(DeliveryAddress);

        await app.StartAsync(TestContext.Current.CancellationToken);

        return app;
    }
```

The comment ADR-052 names is **cut and rewritten**, and the test renamed for
the client it is actually about:

```csharp
    [Fact]
    public async Task The_BFF_service_account_carries_no_permission_claim()
    {
        (_, string token) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        JwtSecurityToken jwt = Tokens.ReadJwtToken(token);

        // §11.4's vocabulary is a person's by default, and this client is the
        // case that holds: the BFF's hop reads what a product listing already
        // publishes, so Catalog's gRPC service asks for authentication and
        // deliberately not a permission. ADR-052 names the exception rather
        // than widening the rule — a host holds one only where the read
        // crosses subjects, and this one does not.
        jwt.Claims.ShouldNotContain(c => c.Type == PermissionClaim.Type);
    }

    [Fact]
    public async Task The_worker_client_is_issued_exactly_the_grant_the_record_names()
    {
        (bool granted, string token) = await keycloak.ClientCredentialsAsync(
            KeycloakFixture.WorkerClient,
            KeycloakFixture.WorkerSecret);

        granted.ShouldBeTrue(
            "the realm must hold shipping-worker with service accounts enabled (ADR-052)");

        JwtSecurityToken jwt = Tokens.ReadJwtToken(token);

        // The audience first: without it the permission below is carried in a
        // token no service validates, and the read fails for the other reason.
        jwt.Audiences.ShouldContain(AuthenticationExtensions.Audience);

        // EXACTLY, not ShouldContain. ADR-052 sizes this credential by what it
        // reads when it is stolen and makes the client refuse a token whose set
        // here is wider than the one role — so a realm that granted more has to
        // fail somewhere, and this is the assertion that says the realm did
        // not. Keycloak's own defaults live in realm_access and on the account
        // client, which this mapper does not read.
        string[] permissions =
        [
            .. jwt.Claims.Where(c => c.Type == PermissionClaim.Type).Select(c => c.Value)
        ];

        permissions.ShouldBe(["orders:delivery-address"]);
    }

    [Fact]
    public async Task A_service_requiring_the_permission_accepts_the_worker_and_refuses_the_BFF()
    {
        (_, string worker) = await keycloak.ClientCredentialsAsync(
            KeycloakFixture.WorkerClient,
            KeycloakFixture.WorkerSecret);
        (_, string bff) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        await using WebApplication service = await ServiceValidatingTheRealm();
        using HttpClient client = service.GetTestClient();

        (await StatusOfAsync(client, worker)).ShouldBe(HttpStatusCode.OK);

        // The refusal, and the BFF rather than an unrelated client on purpose:
        // its token carries the same issuer, the same signing key AND the same
        // audience, so a 403 here can only be the permission doing the work.
        // An unrelated client would be refused at the audience and prove
        // nothing about the grant (ADR-052).
        (await StatusOfAsync(client, bff)).ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<HttpStatusCode> StatusOfAsync(HttpClient client, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/address");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        return response.StatusCode;
    }
```

The class's own doc block is rewritten whole, because its subject widened and
because the block as it stands cannot take another line: it runs twenty lines
from `/// <summary>` to `/// </remarks>`, with `<b>` in its second paragraph,
and the comment gate judges an added line as its whole block (Global
Constraints). Every one of those twenty lines is replaced, in the same shape
PR-1's Task 3 gives the template — the block's first line and its length are
the anchor, and the After replaces exactly those lines. Its third sentence
also goes with the block: "every service would reject the platform's one
permitted synchronous hop" is the §11.5 sentence Task 7 Step 1 amends, and
the hop this PR adds makes it untrue. After:

```csharp
/// <summary>
/// §11.5's whole argument, against a real Keycloak because realm configuration
/// compiles the same right or wrong: the scope becomes an audience, the
/// audience is what a service validates, and neither is granted to a client
/// the realm merely holds. The negative half matters more: a mapper that put
/// the audience on every token would pass the first test and hand the
/// platform to any client in the realm. Since ADR-052 the realm holds two
/// credentialed clients, and the second is proved both ways here — a grant is
/// a claim about what a token carries, and only a real Keycloak carries one.
/// </summary>
```

Ten lines, no emphasis, and `[Collection(nameof(KeycloakCollection))]` follows
it as before.

- [ ] **Step 2: Run to see them fail**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~KeycloakIdentityTests"
```

This class is in `KeycloakCollection`, which carries
`[Trait("Category", "Integration")]` — it needs a running Docker daemon and is
never skipped without one (§12.4). Expected, **with Task 3's realm reverted**
as a mutation check: `granted` false in the grant test, and in the service
test a 401 where 200 is expected — Keycloak refuses the unknown client, the
fixture hands back an empty token, and the host challenges it before the
BFF's 403 line is reached. With Task 3 in place: green.

- [ ] **Step 3: Prove the negative half is not vacuous**

Temporarily delete the `service-account-shipping-worker` user from the realm
export, re-run, and expect
`The_worker_client_is_issued_exactly_the_grant_the_record_names` to fail with
an empty permission set and
`A_service_requiring_the_permission_accepts_the_worker_and_refuses_the_BFF` to
answer 403 for both tokens. Restore the user. This is the only mutation that
tells a realm which grants the role from one that merely holds it.

- [ ] **Step 4: The secret scan, for the fixture's new constant**

`KeycloakFixture.WorkerSecret` is a credential-shaped name assigned a literal,
so the scan reports it as `credential-assignment` and the render refuses
without an entry. That file carries none today — the BFF's default lives in
`KeycloakIdentityTests.cs` — so this is a new path in the allow-list:

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Take the fingerprint from the scan's own output and add one line to
`.github/secret-scan/allowed/tests.txt`, beside the other `Web.Bff.Tests`
entries:

```
tests/Web.Bff.Tests/KeycloakFixture.cs | credential-assignment | <fingerprint> | ADR-052's second local client default, held where the fixture and the realm meet.
```

Re-run; expected: exit 0 and no unmatched entry.

- [ ] **Step 5: Commit**

```bash
dotnet test tests/Web.Bff.Tests
git add tests/Web.Bff.Tests .github/secret-scan/allowed/tests.txt
git commit -m "test(identity): shipping-worker's grant is proved both ways against a real Keycloak"
```

---

### Task 7: The chapters, the two charts, and the two maps

**Files:**
- Modify: `docs/backend-architecture/11-identity-authorization.md` — §11.5's
  table of realm objects, the callout above it, and the section's three
  sentences that count one host
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.4's
  required-for-some-hosts paragraph and three rows
- Modify: `docs/secrets.md` — the rotation sentence, the client-secret
  procedure and the local-development exception table
- Modify: `docs/repo-map.md` — Catalog's and Ordering's rows
- Modify: `CLAUDE.md` — the tree's Catalog and Ordering lines
- Modify: `deploy/helm/ordering/values.yaml` — the header comment and the
  second port
- Modify: `deploy/helm/catalog/Chart.yaml` and `deploy/helm/catalog/values.yaml`
  — the two "one gRPC server" claims
- Modify: `deploy/helm/smoke.sh` — the listener comparison, which reads one
  service's `appsettings.json` and renders one chart

- [ ] **Step 1: §11.5's table of realm objects**

The row after `web-bff`'s:

```
| Client `shipping-worker` | Service accounts enabled, `commerce-api` a **default** client scope, the client role `orders:delivery-address` on its service account | The second synchronous coupling, and the first grant a host holds ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)). The role is what the `permission` mapper emits for a service account, so without it the token is valid and the read is 403 |
```

**The callout above the table moves at both ends**, because this pull request
builds one of the two hosts it calls unbuilt, and amending the closing
sentence alone would leave the opener counting the other way. Its opener
reads today:

> **Two more hosts are decided, and neither is built.**
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
> gives Shipping a client that reads a delivery address from Ordering and
> Notifications one that reads a mailbox from Keycloak, because
> [ADR-035](adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
> left neither value a way to arrive by event.

and becomes:

> **One more host is decided and not built.** Shipping's client is minted
> here, in the pull request that gives Ordering the method it reads, and is
> first used by the pull request that gives Shipping's worker
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s
> address read; Notifications' is still owed, and reads a mailbox from
> Keycloak, because
> [ADR-035](adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
> left neither value a way to arrive by event.

The middle of the callout — that the count of hosts holding a client secret is
the count of synchronous couplings, and that it moves only by a decision
saying what each new secret reads when it is stolen — is the argument this
pull request relies on, and not a word of it moves. The callout's closing
sentence, "Each client joins the table below with the service that uses it.",
becomes "Shipping's joined it with Ordering's method rather than with
Shipping, because the grant is the address owner's to serve." The clause
naming what Notifications is still owed goes to the opener with the count and
is not repeated here.

**The section's three counting sentences move with the table**, because the
row, the count and the prose are one claim and splitting them across pull
requests leaves the paragraph arguing against its own table. ADR-052's §11.5
row names both halves; the spec's section 13 assigns the pair here.

The paragraph after the grant's opening reads today:

> **In this blueprint that is exactly one host: the BFF** (§9.7). The gateway
> forwards the caller's token unchanged rather than exchanging it for one of
> its own; Ordering and Catalog exchange events over the broker and read local
> projections ([§6.4](06-cqrs.md), ADR-002), so neither ever presents itself to
> the other.

and becomes:

> **In this blueprint that is two hosts: the BFF** (§9.7) **and Shipping's
> worker** ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).
> The gateway forwards the caller's token unchanged rather than exchanging it
> for one of its own; every other service exchanges events over the broker and
> reads local projections ([§6.4](06-cqrs.md), ADR-002), so none of them ever
> presents itself to another.

Three lines below that paragraph, and inside the same claim, is the sentence
that fixes the count at one. It stands between the amended paragraph and the
callout that already argues what moving the count costs, so leaving it reads
as the section contradicting itself twice over. It reads today:

> That is not a simplification for the sake of the example — it is what ADR-002
> and ADR-017 add up to. The mechanism below is worth understanding precisely
> because the number of hosts using it is the number of synchronous couplings in
> the platform, and both are meant to stay at one. "Every host gets the full
> identity block" is the natural-looking generalisation and the wrong one; so is
> reading this section and concluding the services talk to each other.

and becomes:

> That is not a simplification for the sake of the example — it is what ADR-002
> and ADR-017 add up to. The mechanism below is worth understanding precisely
> because the number of hosts using it is the number of synchronous couplings in
> the platform, and the callout below says what moving that number costs. "Every
> host gets the full identity block" is the natural-looking generalisation and
> the wrong one; so is reading this section and concluding the services talk to
> each other.

The replacement cites the callout rather than repeating its clause, because
the callout is the owner of what a second secret costs and a second copy three
lines above it is the restatement `docs/change-locality.md` forbids.

The sentence in *The scope has to become an audience* — "Catalog would reject
the platform's only permitted synchronous hop, at the one moment there is no
user to blame it on." — becomes "Catalog would reject a synchronous hop the
platform does permit, at the one moment there is no user to blame it on." The
paragraph below it, which says the BFF's service-account client needs the
scope assigned as default, gains "and so does Shipping's, for the same reason
and by the same mapper (ADR-052)".

- [ ] **Step 2: §15.4**

§15.4's third paragraph is amended as a whole and not by its bold phrase, and
the reason is the grammar. "which in this blueprint is **every host except the
BFF**" has "one that does not" for its antecedent, so swapping the naming
clause for the hosts that *do* call a peer inverts the sentence: it would
declare the keys meaningless for the two hosts that are mandated to hold them.
The sentence after it names Ordering and Catalog as the whole of the rest,
which stops being the list once a third service is the exception. Both move
together. It reads today:

> **Required-for-some-hosts is a third category, and the mistake it invites
> runs the other way.** `Identity__Client__*` is mandatory for a host that
> calls another service and meaningless for one that does not — which in this
> blueprint is **every host except the BFF**. The gateway forwards the caller's
> token rather than minting its own; Ordering and Catalog talk over the broker
> and read local projections ([§6.4](06-cqrs.md), ADR-002). One set of
> credentials in the whole platform is what "async by default" looks like in
> the secrets inventory.

and becomes:

> **Required-for-some-hosts is a third category, and the mistake it invites
> runs the other way.** `Identity__Client__*` is mandatory for a host that
> calls another service and meaningless for one that does not — which in this
> blueprint is **every host but the BFF and, since
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md),
> Shipping's worker**. The gateway forwards the caller's token rather than
> minting its own; every other service exchanges events over the broker and
> reads local projections ([§6.4](06-cqrs.md), ADR-002). Two sets of
> credentials in the whole platform, and the count is the point: it is the
> number of synchronous couplings, and it moved by a decision that said what
> the second one reads when it is stolen.

The sentence beginning "Supplying the rest "for consistency" is not harmless
padding" follows unchanged, and is the reason the paragraph still belongs to
the over-supply category rather than becoming a second inventory.

The three rows' Required column names the obligation's shape rather than
today's snapshot, because §15.4's own rule is that **a key joins when a host's
code reads it** and Shipping's host reads none of these until the pull request
that gives it the address read:

```
| `Identity__Client__ClientId` | Config | Helm `identity.clientId` | ✓ **for a host that calls a peer** — the BFF ([§9.7](09-messaging.md), [§11.5](11-identity-authorization.md)), and Shipping's worker from the pull request that gives it ADR-052's address read |
| `Identity__Client__Scope` | Config | Helm `identity.scope` | ✓ **for a host that calls a peer**, as above |
| `Identity__Client__ClientSecret` | Secret | `web-bff-identity` secret; one per host | ✓ **for a host that calls a peer**, as above |
```

- [ ] **Step 3: `docs/secrets.md`**

The *Rotation* opening sentence gains the second host, by its
`Identity__Client__ClientSecret` clause and not as a whole sentence. The
clause

```markdown
`Identity__Client__ClientSecret`, for the
BFF, the only host that calls a peer synchronously
([§9.7](backend-architecture/09-messaging.md),
[§11.5](backend-architecture/11-identity-authorization.md), ADR-017)
```

becomes

```markdown
`Identity__Client__ClientSecret`, for the
BFF and, since
[ADR-052](backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md),
for Shipping's worker — the two hosts that call a peer synchronously
([§9.7](backend-architecture/09-messaging.md),
[§11.5](backend-architecture/11-identity-authorization.md), ADR-017)
```

and every other clause of the sentence is left exactly as found; the line
breaks above are the file's rather than the clause's, so rewrap the paragraph
at 80 columns once the substitution is made. **The edit is
clause-level because the sentence is a list two pull requests append to.** PR-2
adds the carrier's clause to the same sentence — "and `Carrier__ApiKey`, for
Shipping's carrier behind §3.2's anti-corruption layer" — and the spec puts
neither PR first, so a replacement quoting the whole sentence would silently
drop the carrier whenever PR-2 landed ahead of this one. Where PR-2 has landed,
its clause is already there and stays; where it has not, it arrives beside this
one.

*A client secret*'s steps 3 and 4 are per host rather than the BFF's alone,
and both move: step 3 names the pods to restart and step 4 names the proof
they came back authenticating, so a procedure that is half one-host sends the
operator to the wrong deployment and then tells them to watch the wrong
metric.

"3. Wait for External Secrets to reconcile, then restart the pods of the host
whose secret this is — configuration is read at startup, so a reconciled
Secret does not reach a running process."

"4. Confirm the host is authenticating. For the BFF that is pricing calls to
Catalog succeeding; for Shipping's worker it is shipments leaving `Pending`,
and `shipping.address.refused` staying flat — ADR-052 counts a refused
credential separately from an outage for exactly this moment."

The bold sentence under the list — "**Step 3 is the one that gets skipped**" —
is unchanged: it is about the step's position, not about whose pods it names.

The local-development exception table gains a row, in the PR that mints the
secret:

```
| Shipping worker client secret | `local-dev-shipping-secret`, in the realm export; the seam in front of it arrives with the host that reads it |
```

and the paragraph below the table, which explains which rows carry a `${…}`
seam, gains one clause: "Shipping's client secret has no variable in front of
it yet for a reason of sequence rather than of design — the realm holds the
value from the change that minted the client, and the Compose seam arrives with
the host that posts it."

- [ ] **Step 4: The two maps**

`docs/repo-map.md`:

```
src/Services/Catalog/        §4.1's project set — Domain, Application,
                             Infrastructure, Migrator, Api. The first real
                             service, the scaffold's template, and the first
                             of the platform's two gRPC servers
```

and Ordering's row gains a final clause: "…and, since ADR-052,
`DeliveryAddresses.Get` on a second HTTP/2-only port — the address a Shipping
worker reads, behind a permission no person holds".

`CLAUDE.md`'s tree:

```
src/Services/Catalog/        §4.1's five projects; the first gRPC server
src/Services/Ordering/       the same five, plus §5's aggregate, §9.6's saga
                             and ADR-052's gRPC address read
```

- [ ] **Step 5: The two charts**

`deploy/helm/ordering/values.yaml`'s header and `ports`:

```yaml
# Ordering, and the chart §15.3 prints. Two ports since ADR-052: §10.2's REST
# surface, and the HTTP/2-only endpoint that serves DeliveryAddresses.Get to a
# Shipping worker. A cleartext Kestrel endpoint cannot serve HTTP/1.1 and h2c
# at once — measured, and argued in Ordering.Api/appsettings.json.
```

```yaml
ports:
  - name: http
    containerPort: 8080
  # 8081 is cluster-internal: no route reaches it (§10.2 routes HTTP), the
  # Ingress is disabled, and the RPC on it requires orders:delivery-address
  # (ADR-052).
  - name: grpc
    containerPort: 8081
```

**It lands here rather than in PR-7, and the reason is whose chart it is.**
PR-7 ships *Shipping's* chart; Ordering's belongs to Ordering's slice, and the
statement it makes — "Ordering serves no gRPC" — stops being true in this
pull
request and in no other. `probes.probePort` stays `http`: the health endpoints
are on the REST surface, and an HTTP/2-only endpoint answers an HTTP/1.1 probe
with a 400, which reads as a dead pod.

`deploy/helm/catalog/Chart.yaml`'s description: "Catalog (§4.1) — its API,
its
migrator hook, and the first of the platform's two gRPC servers (§9.7,
ADR-052)." `deploy/helm/catalog/values.yaml`'s `ports` comment keeps its
measurement and loses its claim of uniqueness: "…so Catalog declares a second,
HTTP/2-only endpoint for the BFF's synchronous hop (§9.7). Ordering declares
one of its own for ADR-052's address read."

**`smoke.sh`'s credential assertions stay green, and that is not the same as
nothing moving.** Those assertions count charts declaring
`clientCredentials: true` and workloads rendering
`Identity__Client__ClientSecret`; neither chart gains one here, and Shipping
has no chart until PR-7, so they and `_helpers.tpl`'s `fail` naming `web-bff`
are PR-7's. The section immediately above them is a different matter, and it
is the next step.

- [ ] **Step 6: `smoke.sh` compares every pinned listener, not Catalog's**

*The Service forwards to a port something is listening on* reads its listeners
out of Catalog's `appsettings.json` and compares them against Catalog's render
alone. A second service pinning its own ports is therefore covered by nothing:
the `grpc` port added in step 5 could name 8082, or the settings file could
name 9090, and the section would report `ok` twice and say nothing about
either. **A gate that quietly stops covering the newest surface is the failure
`CLAUDE.md` names**, and the defence is to make the section's subject the set
of services that pin their own ports rather than the one that did first.

Before:

```bash
# The routing gate above compares caller URLs with rendered Service ports and
# never looks at the process behind `targetPort`. Catalog declares its two
# Kestrel endpoints in its own appsettings.json (§9.7: a cleartext port cannot
# serve HTTP/1.1 and h2c at once), so moving the h2c listener there would
# deploy a Service forwarding to a closed port.
grep -ohE 'http://0\.0\.0\.0:[0-9]+' "$ROOT/src/Services/Catalog/Catalog.Api/appsettings.json" |
    sed -E 's|.*:([0-9]+)|\1|' | sort -u >"$OUT/listeners.txt"

if [ ! -s "$OUT/listeners.txt" ]; then
    fail 'no Kestrel endpoints found in Catalog appsettings.json — the parse, not the chart, is wrong'
else
    while read -r port; do
        check "catalog-api listens on $port and the chart declares it" \
            grep -q "containerPort: $port$" "$OUT/catalog.yaml"
    done <"$OUT/listeners.txt"
fi

# And the other direction, so a chart port with nothing behind it is caught too.
awk '/^kind: Deployment$/ { in_dep = 1 } in_dep && /containerPort:/ { print $2 }' \
    "$OUT/catalog.yaml" | sort -u >"$OUT/declared.txt"
missing="$(comm -23 "$OUT/declared.txt" "$OUT/listeners.txt")"
if [ -z "$missing" ]; then
    pass 'and declares no port Catalog does not listen on'
else
    fail "chart declares port(s) Catalog has no listener for: $(echo "$missing" | tr '\n' ' ')"
fi
```

After:

```bash
# The routing gate above compares caller URLs with rendered Service ports and
# never looks at the process behind `targetPort`. A service declaring
# Kestrel:Endpoints owns its ports outright — ASPNETCORE_URLS and
# ASPNETCORE_HTTP_PORTS both lose to that section (§9.7) — so a listener moved
# there and not in the chart deploys a Service forwarding to a closed port.
# Every chart is asked, through src_of, and the number that answered is
# asserted below: a service that starts pinning its own ports is covered the
# day it does, and a search that stops finding any fails rather than passing
# quietly.
pinned=0
for chart in $SERVICE_CHARTS; do
    settings="$(grep -rl '"Kestrel"' --include=appsettings.json "$(src_of "$chart")" || true)"
    [ -n "$settings" ] || continue

    if [ "$(printf '%s\n' "$settings" | wc -l)" -ne 1 ]; then
        fail "$chart pins ports in more than one appsettings.json — the search, not the chart, is wrong"
        continue
    fi

    pinned=$((pinned + 1))
    grep -ohE 'http://0\.0\.0\.0:[0-9]+' "$settings" |
        sed -E 's|.*:([0-9]+)|\1|' | sort -u >"$OUT/$chart-listeners.txt"

    if [ ! -s "$OUT/$chart-listeners.txt" ]; then
        fail "no Kestrel endpoint parsed out of $settings — the parse, not the chart, is wrong"
        continue
    fi

    while read -r port; do
        check "$chart listens on $port and its chart declares it" \
            grep -q "containerPort: $port$" "$OUT/$chart.yaml"
    done <"$OUT/$chart-listeners.txt"

    # And the other direction, so a chart port with nothing behind it is caught too.
    awk '/^kind: Deployment$/ { in_dep = 1 } in_dep && /containerPort:/ { print $2 }' \
        "$OUT/$chart.yaml" | sort -u >"$OUT/$chart-declared.txt"
    missing="$(comm -23 "$OUT/$chart-declared.txt" "$OUT/$chart-listeners.txt")"
    if [ -z "$missing" ]; then
        pass "and $chart declares no port it does not listen on"
    else
        fail "$chart's chart declares port(s) it has no listener for: $(echo "$missing" | tr '\n' ' ')"
    fi
done

if [ "$pinned" -eq 0 ]; then
    fail 'no chart pins its own Kestrel endpoints — the search, not the charts, is wrong'
else
    pass "the listener comparison covered $pinned chart(s) that pin their own endpoints"
fi
```

Four things in that block are the file's own idioms rather than choices, and
each is load-bearing:

- **`src_of` already exists**, defined once near the top of the file where the
  per-chart source checks begin, and it is declared there as
  data because the gateway and the BFF are not under `src/Services`. Reusing it
  is what keeps one mapping rather than two that can disagree.
- **`--include=appsettings.json` matches the basename exactly**, so
  `appsettings.Development.json` is not read: a development override that
  moved a port would be a claim about a machine, not about the image the chart
  deploys. `Gateway.Api`'s `appsettings.json` has no `Kestrel` section and is
  skipped by the grep, which is the right answer and not an omission.
- **`|| true` on the `grep -rl`** because `grep` exits 1 on no match and the
  script runs under `set -e`; without it the first chart that pins nothing
  ends the run with a success-shaped exit and every section below it unrun.
- **`comm` needs both sides sorted**, which `sort -u` gives them, and the
  per-chart file names keep two charts from overwriting each other's — a
  single `listeners.txt` reused round the loop would compare Ordering's chart
  against whichever list was written last.

Run it against the tree step 5 leaves, and then break it on purpose: a widened
gate nobody has seen red is a widened gate nobody has tested, which is the
same fail-open shape this step exists to close.

```bash
bash deploy/helm/smoke.sh
```

Expected, with step 5's two ports in place: `ok` for 8080 and 8081 on each of
`catalog` and `ordering`, `ok` for the reverse direction on both, and
`the listener comparison covered 2 chart(s) that pin their own endpoints`.
Deleting `containerPort: 8081` from `deploy/helm/ordering/values.yaml` and
re-running must fail the first direction for `ordering`; restore it.

- [ ] **Step 7: Audit; commit**

```bash
bash deploy/helm/smoke.sh
py -3.12 deploy/observability/check.py
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: all clean — the first because the widened comparison sees both charts,
the second because no alert or runbook moved, and the third because `smoke.sh`
is a `.sh` file the gate reads and its two rewritten blocks come out at ten
lines and one. A bare `#` line does not end a block — the gate's own README
defines one as a run of lines holding nothing but comment — so the section
header's argument is written as a single nine-line block rather than as two
halves around a blank comment, and the `# ----` rule above it is comment too,
which is where the tenth line comes from: exactly the limit, not over it. Then
run `/check-links` and `/validate-blueprint`.

```bash
git add docs deploy/helm CLAUDE.md
git commit -m "docs: §11.5's realm table, §15.4's client rows and the two maps name the second credentialed host"
```

---

### Task 8: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green, with a running Docker daemon. The
  three container suites this PR touches are `Ordering.Api.Tests`,
  `Web.Bff.Tests`' Keycloak collection and `Common.Web.Tests`.
- [ ] The gates none of the above runs, each on its own:

```bash
py -3.12 -m unittest discover -s deploy/keycloak
py -3.12 deploy/keycloak/realm_check.py check --kind local --realm deploy/compose/keycloak/realm-export.json
py -3.12 .github/secret-scan/secret_scan.py
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
bash deploy/helm/smoke.sh
```

`--base` is required and has no default: the gate reads the pull request's own
diff, so without it the run refuses rather than passing on nothing.

- [ ] Under Compose, with the realm imported fresh (`docker compose down -v`
  first, because Keycloak imports once): fetch a `shipping-worker` token and
  call the method by hand.

**The probe runs in-network, and that is not a convenience.**
`deploy/compose/services/ordering.yml` maps `127.0.0.1:5101` to the container's
8080 — the Http1 REST endpoint — and publishes 8081 nowhere, so `grpcurl`
against `localhost:5101` would reach the REST surface and fail at the protocol
rather than at the method. Dial the container port from a container on the
Compose network instead:

```bash
TOKEN=$(docker compose -f deploy/compose/docker-compose.yml exec -T ordering-api \
  curl -s -d grant_type=client_credentials -d client_id=shipping-worker \
  -d client_secret=local-dev-shipping-secret \
  http://keycloak:8080/realms/commerce/protocol/openid-connect/token | jq -r .access_token)
docker run --rm --network deploy_default -v "$PWD/src/Services/Ordering/Ordering.Api/Protos:/protos:ro" \
  fullstorydev/grpcurl -plaintext -H "authorization: Bearer $TOKEN" \
  -import-path /protos -proto delivery_addresses.proto \
  -d '{"order_id":"<a placed order>"}' \
  ordering-api:8081 ordering.delivery.v1.DeliveryAddresses/Get
```

Take the network name from `docker compose ... config` rather than from this
plan; reflection is not registered, which is why the `.proto` is mounted.
Publishing 8081 temporarily is the alternative, and it is not committed.
Record the reply, and the `PermissionDenied` a `demo` token gets, in the PR
body.

- [ ] PR body: `| Class | A+D+E |`, touch set from the Global Constraints, one
  path per cell and the reasons under the table. Then `/ship`.

## Self-review

**Spec coverage.**

- Section 2's bullet — Ordering gains `DeliveryAddresses.Get` under
  `orders:delivery-address`, and the realm gains `shipping-worker` holding that
  one role → Tasks 1, 3 and 4.
- Section 3's row — `delivery_addresses.proto` and its service (Tasks 1, 4),
  the query behind it (Task 2), the permission (Task 4), the realm's role and
  client (Task 3), `realm_check.py`'s predicate (Task 3), the gateway test
  (Task 5), `docs/secrets.md`'s rows (Task 7), the Keycloak-issued-token tests
  in both directions (Task 6). Class A+D+E, and no Shipping path is touched.
- Section 9's address port, read from the server's side — ADR-052's five
  outcomes: the answer (Task 4's third test), `NotFound` for each of the three
  facts that mean "does not exist" (Task 2's handler, Task 4's three tests),
  `Unauthenticated` and `PermissionDenied` (Task 4). The transient and refused
  classifications are the *client's* mapping and are PR-5's.
- Section 10's client secret — minted in the realm with its documented local
  default and its `docs/secrets.md` rows (Tasks 3 and 7). Of the five
  places, the
  inventory is met here; Compose and the fixture arrive with the host that
  reads the key (PR-5) and the chart's with the chart (PR-7).
- Section 12's five tests — `Unauthenticated`, `PermissionDenied` with a
  user's
  token holding every user permission, and the address with the client's
  (Task 4); a token Keycloak issued to `shipping-worker` accepted and one
  issued to a client without the role refused (Task 6).
- Section 13's chapters — §11.5's table of realm objects, both ends of the
  callout above it (its opener, which counts two hosts decided and neither
  built, and its closing sentence) and the three
  sentences that count its hosts, `docs/secrets.md`'s rotation sentence, both
  halves of the client-secret procedure and its local-default row, §15.4's
  required-for-some-hosts paragraph and its three client rows,
  `docs/repo-map.md`'s and `CLAUDE.md`'s gRPC-server halves and
  the two Catalog chart claims → Task 7. Two places sit outside ADR-052's
  table and spec section 13 names them there rather than in it: the Compose
  README's host-run port recipe, which says Catalog is the one service pinning
  its own ports and gives Ordering no `Kestrel__Endpoints__…__Url` exports →
  Task 1 step 5; and `smoke.sh`'s listener comparison, which reads one
  service's `appsettings.json` → Task 7 step 6.
  ADR-052's `realm-export.json` row —
  `web-bff`'s description as the only client holding credentials — is Task 3
  step 2, in the same edit that adds the second such client; the realm's
  internationalisation half of that row is left alone, and Task 3 says why.

**Gates this PR turns red, and the task that turns each green.**

| Gate | Red because | Green in |
|---|---|---|
| `RealmImportTests.The_permission_vocabulary_is_a_closed_set_of_client_roles` | `orders:delivery-address` is a seventh role | Task 3, step 1 |
| `RealmImportTests.No_client_ships_a_secret_but_the_one_whose_grant_needs_one` | a second client ships one; renamed to `…_the_ones_whose_grants_need_one` | Task 3, step 1 |
| `RealmClientTests.It_is_the_only_service_account_client_in_the_realm` | a second service-account client; renamed to `The_service_account_clients_are_exactly_the_hosts_that_call_a_peer` | Task 3, step 1 |
| `Ordering.Api.Tests.GrantablePermissionTests` | reflection finds a constant the realm cannot grant | Task 3, which lands the role before Task 4 adds the constant |
| `.github/secret-scan` | a new local default in the realm export and in `RealmImportTests`, and the fixture's own constant | Task 3, step 6, and Task 6, step 4 |
| `deploy/keycloak/realm_check.py` | its new predicate requires exactly one `shipping-worker` | Task 3, steps 2–3 |
| `KeycloakIdentityTests.The_service_account_carries_no_permission_claim` | its comment says the vocabulary "belongs to people, not to hosts"; renamed to `The_BFF_service_account_carries_no_permission_claim` and the comment cut and rewritten | Task 6, step 1 |

`_helpers.tpl`'s `fail` and `smoke.sh`'s four credential assertions are
**PR-7's**, and this PR turns neither red: they are about a chart declaring
client credentials, Shipping has no chart until PR-7, and no chart here gains
the capability.

**One gate stays green and is widened anyway, which is the row that matters
most.** `smoke.sh`'s *The Service forwards to a port something is listening on*
compares Catalog's `appsettings.json` with Catalog's render, so Ordering's
second port and its second listener would both land uncovered and the section
would report `ok` throughout. That is the shape `CLAUDE.md` calls this
repository's most-repeated failure, and the answer it gives is a check whose
subject is what the gate is looking at: Task 7 step 6 makes the section loop
over every chart whose source pins its own endpoints and assert how many did.

**Type consistency.** `GetDeliveryAddressRequest`, `GetDeliveryAddressReply`
and `DeliveryAddresses.DeliveryAddressesBase` are produced by Task 1 and
consumed by Tasks 4 and 5 under those spellings; the reply's `post_code` field
is generated as `PostCode` and is spelt that way in every consumer.
`GetDeliveryAddressQuery` and `DeliveryAddressView` are produced by Task 2 and
consumed by Task 4; `DeliveryAddressView.PostalCode` keeps the domain's
spelling and is mapped onto `PostCode` in one place.
`OrderingPermissions.DeliveryAddress` is produced by Task 4 and read by Task 4's
tests and by `GrantablePermissionTests` through reflection;
`KeycloakFixture.WorkerClient` and `.WorkerSecret` are produced by Task 6 and
read in the same task. `realm_check.WORKER_CLIENT` and `check_worker_client`
are produced and consumed in Task 3. The realm's `shipping-worker` and
`local-dev-shipping-secret` are written once in Task 3 and named as literals in
`RealmImportTests`, `RealmClientTests` and `KeycloakFixture`, each with a
comment saying why the suite cannot read the owner's constant.

**Left to a later PR.**

- **Every caller.** `IDeliveryAddressSource`, `AddressHop`, the generated
  client, `ClientCredentialsHandler` inside its resilience pipeline and
  `shipping.address.refused` are PR-5's. So are the three `Identity__Client__*`
  keys and `AddressSource__BaseUrl` on Shipping's Compose unit and test
  fixture, `.env.example`'s `SHIPPING_CLIENT_SECRET`, and a
  `RealmClientTests`-shaped assertion that the realm and that unit hold the
  same secret — the host that owns the client id is the side that makes it.
- **§9.7's sentence** that the pricing hop is the platform's one synchronous
  call between its services, **§2.2's diagram**,
  **`deploy/compose/README.md`'s description of that call**
  and **`docs/runbooks/latency.md`** all describe a *call*, and no code makes
  the second one until PR-5. Spec section 13 assigns §2.2 to PR-5, and the
  other three go with it — as do §12's sentence, §14.1's and §14.2's, §11.7's
  erasure step, and the BFF halves of `docs/repo-map.md` and `CLAUDE.md`.
  **The Compose README is split, and the split is by fact rather than by
  file.** Its host-run port recipe is not about a call at all: it says which
  services pin their own ports and what a host run has to export, and this
  pull request is what makes both wrong, so Task 1 step 5 takes it. The
  sentences describing the BFF's hop are untouched here and stay PR-5's.
- **§15.1's "one client secret in the whole platform"**, which spec section 13
  assigns to **PR-7**: that sentence describes what `smoke.sh` asserts, and
  `smoke.sh`'s credential assertions do not move until a second credentialed
  chart renders. Amending the sentence here would leave it disagreeing with
  the gate it describes for three pull requests. §15.4's rows are this PR's,
  because they are the inventory rather than the gate.
- **§4.1's tree comment and `ServiceOptions`' remark** about the one host with
  client credentials are PR-3's, per spec section 13.
- **Whether `DeliveryAddresses.Get` earns a linked-file contract** of
  ADR-023's kind is ADR-052's explicit "judged with Shipping": only a consumer
  can author one, and there is none until PR-5.
- **§11.7's erasure consumer**, which is what will actually clear an address in
  place. This PR designs against it — the handler's blank-address branch and
  its test — and builds none of it, because §11.7's extension is owed whole.
- **`deploy/helm/shipping`**, the `carrier` and client-credentials
  capabilities, and `smoke.sh`'s lists: PR-7's.
