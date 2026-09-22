# Shipping PR-3 — client credentials move to Common.Infrastructure — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put §11.5's client-credentials grant where a second host can reach
it. `ITokenCache`, `CachingTokenClient`, `ClientCredentialsHandler` and
`ServiceIdentityOptions` leave `Web.Bff`, their tests leave `Web.Bff.Tests`,
and the BFF goes on binding `Identity:Client` and registering all four from
its own composition root. **No behaviour moves**: every assertion that held
before this PR holds after it, in the suite it moves to.

**Architecture:** the four types land in
`src/BuildingBlocks/Common.Infrastructure/Identity/`, namespace
`Common.Infrastructure.Identity`. One dependency cannot travel with them:
`CachingTokenClient` names `Common.Web`'s `AuthenticationExtensions.
AuthorityKey` in two refusal messages, and a building block below `Common.Web`
may not reference it. The name therefore travels as a **value** the
composition root supplies — `AuthorityKeyName`, in `OutboxTable`'s shape,
which carries a schema `Common.Infrastructure` is likewise forbidden to write
down. `Web.Bff` gains a project reference to `Common.Infrastructure`, because
it is the one host with no `*.Infrastructure` project of its own to reach it
through (§10.1).

**Tech Stack:** `Microsoft.Extensions.Http` (new pin — `IHttpClientFactory`,
which `Common.Infrastructure` takes as a package because it holds no framework
reference), `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.
Abstractions`, both already referenced there.

**Spec:** `docs/superpowers/specs/2026-09-22-shipping-service-design.md`,
sections 2 (the bullet moving the BFF's `Identity/` types), 3 (the PR-3 row and
the order 3 → 4) and 13 (§4.1's tree comment and `ServiceOptions`' remark).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **This plan lands as two pull requests, because its honest class is
  three letters and `docs/change-locality.md` admits `A+D+E` alone.**
  Task 1 is **PR-3a, Class E**: `Directory.Packages.props`,
  `src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj`
  and `docs/backend-architecture/appendix-b-licences.md` — the pin, the
  project file that references it, and the register row, which is exactly
  Class E's touch set, and the licence gate does not refuse a package no
  type uses yet. Tasks 2 to 5 are **PR-3b, Class B+D**. Touch set:
  `src/BuildingBlocks/Common.Infrastructure/**`,
  `src/BuildingBlocks/Common.Web/ServiceOptions.cs`, `src/BFF/Web.Bff/**`,
  `tests/Common.Infrastructure.Tests/**`, `tests/Web.Bff.Tests/**`,
  `docs/backend-architecture/04-solution-structure.md`,
  `.github/secret-scan/allowed/tests.txt`. Why each: the two building
  blocks and their suites, the BFF as the one host whose wiring this proves,
  and §4.1's one tree comment are B — `docs/change-locality.md`'s section 3
  says crossing two building-block projects is B rather than A. The
  allow-list is D: the secret scan keys an accepted finding by path, and two
  of the moving test files carry four of them, so a file that moves and an
  entry that does not is a build failure on the entry that now matches
  nothing. PR-3b depends on PR-3a having merged, and each carries its own
  body, class row and touch set.
- Depends on nothing. PR-1 and PR-2 touch no path in the set above, and this
  PR touches no Shipping path; it may land before or after either.
- `Platform.slnx` is unchanged: no project is added or removed.
- **No behaviour moves.** Every message string, every status mapping, every
  cached-token rule and every registration lifetime is what it was. The one
  new type is a carrier for a name the building block may not write down, and
  it exists so that the two refusal messages stay word for word what they are.
- Comments say why and cite the owner. **The comment gate judges an edited
  line as its whole block**, so a doc comment this PR edits must come out at
  ten lines or fewer with no `<b>` and no `**…**`; the blocks it does not
  touch stay as they are.
- Explicit local types, file-scoped namespaces with a blank line after, one
  space before `=`, `=>` and `{`, 120 columns, British spelling. `py -3.12`.
- `git mv` for every file that moves, so the history follows and the comment
  gate judges a renamed file on its changed lines alone.

---

### Task 1: The pin and its register row

**Files:**
- Modify: `Directory.Packages.props` — `Microsoft.Extensions.Http`
- Modify: `src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj` — the reference
- Modify: `docs/backend-architecture/appendix-b-licences.md` — its row

**Interfaces:**
- Produces: `IHttpClientFactory` resolvable from `Common.Infrastructure`,
  which Task 2's `CachingTokenClient` takes.

This task is **PR-3a** on its own, Class E. The licence gate refuses a pin
the register does not name and a registered identity pinned nowhere; it does
not refuse a package no type uses yet, so the three edits are one pull
request and the gate is green after it.

- [ ] **Step 1: Add the pin**

In `Directory.Packages.props`, beside the other `Microsoft.Extensions.*` rows
and after `Microsoft.Extensions.Http.Resilience`:

```xml
    <!-- IHttpClientFactory, which Common.Infrastructure's token client fetches
         its grant over (§11.5). ASP.NET Core's shared framework carries it and
         that building block takes no framework reference, so it pays for the
         contract as a package — the abstractions rows' terms. -->
    <PackageVersion Include="Microsoft.Extensions.Http" Version="10.0.0" />
```

The version is the one every other `Microsoft.Extensions.*` row in that file
carries. Copy it from the neighbouring rows rather than from this plan; a pin
written from a document is a pin with two owners.

- [ ] **Step 2: Add the register row**

In `docs/backend-architecture/appendix-b-licences.md`, beside
`Microsoft.Extensions.Options`:

```markdown
| `Microsoft.Extensions.Http` | MIT | `IHttpClientFactory`, resolved by `CachingTokenClient` to fetch §11.5's grant over its own named client. Rides in the shared framework, which `Common.Infrastructure` does not take — the `Microsoft.Extensions.Hosting.Abstractions` row's terms, one contract over |
```

- [ ] **Step 3: The building block's package reference**

In `src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj`,
beside `Microsoft.Extensions.Options`:

```xml
    <!-- IHttpClientFactory, which the token client fetches §11.5's grant over
         by name rather than over a captured HttpClient: a singleton that held
         one would keep its handler past the factory's rotation. -->
    <PackageReference Include="Microsoft.Extensions.Http" />
```

No `Version=`, and no other change to that file.

- [ ] **Step 4: Run the licence gate and build**

```bash
py -3.12 -m unittest discover -s .github/licence-gate
py -3.12 .github/licence-gate/licence_gate.py
dotnet build src/BuildingBlocks/Common.Infrastructure
```

Expected: both exit 0. Before the register row lands the gate refuses the pin
by name, which is worth seeing once — add the pin, run it, read the refusal,
then add the row.

- [ ] **Step 5: Commit, and open PR-3a**

```bash
git add Directory.Packages.props src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj docs/backend-architecture/appendix-b-licences.md
git commit -m "chore(deps): Common.Infrastructure takes Microsoft.Extensions.Http for the identity types"
```

PR-3a's body carries `| Class | E |` and the three paths above as its touch
set; PR-3b starts from `main` after it merges.

---

### Task 2: The four types move, and the BFF re-points at them

**Files:**
- Move: `src/BFF/Web.Bff/Identity/ITokenCache.cs` →
  `src/BuildingBlocks/Common.Infrastructure/Identity/ITokenCache.cs`
- Move: `src/BFF/Web.Bff/Identity/CachingTokenClient.cs` → the same directory
- Move: `src/BFF/Web.Bff/Identity/ClientCredentialsHandler.cs` → the same
- Move: `src/BFF/Web.Bff/Identity/ServiceIdentityOptions.cs` → the same
- Create: `src/BuildingBlocks/Common.Infrastructure/Identity/AuthorityKeyName.cs`
- Modify: `src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj`
- Modify: `src/BFF/Web.Bff/Web.Bff.csproj` — the project reference
- Modify: `src/BFF/Web.Bff/Dockerfile` — the restore-layer `COPY`
- Modify: `src/BFF/Web.Bff/Program.cs` — the `using`, one comment, one
  registration
- Modify: `tests/Web.Bff.Tests/BffFactory.cs`,
  `tests/Web.Bff.Tests/RecordingTokenCache.cs`,
  `tests/Web.Bff.Tests/OptionsValidationTests.cs` — the `using`
- Modify: `tests/Web.Bff.Tests/CachingTokenClientTests.cs`,
  `tests/Web.Bff.Tests/TokenEndpointSchemeTests.cs` — the `using` and the
  carrier's registration; they move in Task 3
- Test: `tests/Web.Bff.Tests/IdentityRegistrationTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Common.Infrastructure.Identity;

public interface ITokenCache
{
    Task<string> GetAsync(string scope, CancellationToken ct);
}

public sealed record AuthorityKeyName(string Name);

public sealed partial class CachingTokenClient(
    IHttpClientFactory clients,
    IOptions<ServiceIdentityOptions> identity,
    AuthorityKeyName authorityKey,
    TimeProvider clock,
    ILogger<CachingTokenClient> logger) : ITokenCache, IDisposable
{
    public const string HttpClientName = "identity";
}

public sealed class ClientCredentialsHandler(ITokenCache tokens, IOptions<ServiceIdentityOptions> identity)
    : DelegatingHandler;

public sealed class ServiceIdentityOptions
{
    public const string SectionName = "Identity:Client";
}
```

- Consumes: `AuthenticationExtensions.AuthorityKey` and
  `AuthenticationExtensions.Audience`, both still `Common.Web`'s, named only
  from `Web.Bff` and from test projects.

What moves and what stays, file by file — `src/BFF/Web.Bff/Identity/` holds
exactly these four and nothing else, so the directory is gone after this task:

| File | Where it goes | Why |
|---|---|---|
| `ITokenCache.cs` | moves, unchanged but its namespace | the port `ClientCredentialsHandler` holds; no host type in it |
| `CachingTokenClient.cs` | moves, with the `Common.Web` dependency turned into a carried value | §11.5's grant itself — ADR-052's "the code that posts a client secret" |
| `ClientCredentialsHandler.cs` | moves, unchanged but its namespace | the same mechanism's outbound half; names only `ITokenCache` and the options |
| `ServiceIdentityOptions.cs` | moves, with its remark rewritten | it is no longer in a host, so a remark saying "this is the only host that binds it" is false where it stands |
| `Program.cs`'s registrations | **stay** | §4.2 makes `Program.cs` the only composition root, and §15.3's argument is that a binding every host inherits is a credential every host is gated on |
| `PricingHop`, `UpstreamExceptionHandler`, `Endpoints/` | **stay**, untouched | the hop is the BFF's, not the grant's |

- [ ] **Step 1: Write the failing wiring test**

`tests/Web.Bff.Tests/IdentityRegistrationTests.cs`:

```csharp
using Common.Infrastructure.Identity;
using Common.Web;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// The half of §11.5 a host keeps after ADR-052: the grant's code is a
/// building block's, and the names it cannot write down are this host's to
/// supply.
/// </summary>
public class IdentityRegistrationTests
{
    [Fact]
    public void The_host_carries_the_authority_keys_name_to_the_token_client()
    {
        using BffFactory factory = new();

        // Common.Infrastructure may not name Common.Web, so the key's name
        // travels as a value and a refused discovery document says which key
        // to fix. Supplied wrongly, every refusal this host's token client
        // writes names a key nobody can set, and no other test would notice.
        factory.Services.GetRequiredService<AuthorityKeyName>()
            .Name.ShouldBe(AuthenticationExtensions.AuthorityKey);
    }
}
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~IdentityRegistrationTests"`
Expected: compile failure — `Common.Infrastructure.Identity` does not exist.

- [ ] **Step 3: Move the four files**

```bash
mkdir -p src/BuildingBlocks/Common.Infrastructure/Identity
git mv src/BFF/Web.Bff/Identity/ITokenCache.cs \
       src/BuildingBlocks/Common.Infrastructure/Identity/ITokenCache.cs
git mv src/BFF/Web.Bff/Identity/CachingTokenClient.cs \
       src/BuildingBlocks/Common.Infrastructure/Identity/CachingTokenClient.cs
git mv src/BFF/Web.Bff/Identity/ClientCredentialsHandler.cs \
       src/BuildingBlocks/Common.Infrastructure/Identity/ClientCredentialsHandler.cs
git mv src/BFF/Web.Bff/Identity/ServiceIdentityOptions.cs \
       src/BuildingBlocks/Common.Infrastructure/Identity/ServiceIdentityOptions.cs
```

In all four, `namespace Web.Bff.Identity;` becomes
`namespace Common.Infrastructure.Identity;`. In `ITokenCache.cs` and
`ClientCredentialsHandler.cs` that is the whole change: neither names a
`Web.Bff` type, and their doc comments stay exactly as they are, which is what
keeps the comment gate off blocks it would otherwise judge whole.

- [ ] **Step 4: Write the carrier**

`src/BuildingBlocks/Common.Infrastructure/Identity/AuthorityKeyName.cs`:

```csharp
namespace Common.Infrastructure.Identity;

/// <summary>
/// The name of the configuration key a host read its authority from, so that
/// a refusal can say which key to fix. A registered value rather than a
/// constant here: §11.3's registration in <c>Common.Web</c> owns the name,
/// this building block sits below that assembly and may not reference it, and
/// <see cref="Outbox.OutboxTable"/>'s schema travels the same way (§9.4).
/// </summary>
public sealed record AuthorityKeyName(string Name);
```

- [ ] **Step 5: Take the `Common.Web` dependency out of `CachingTokenClient`**

Four edits, all on code lines. Drop `using Common.Web;` from the file's
header. Add the carrier to the primary constructor, after `identity`:

```csharp
public sealed partial class CachingTokenClient(
    IHttpClientFactory clients,
    IOptions<ServiceIdentityOptions> identity,
    AuthorityKeyName authorityKey,
    TimeProvider clock,
    ILogger<CachingTokenClient> logger) : ITokenCache, IDisposable
```

`DiscoverTokenEndpointAsync` and `Unusable` read it, so both lose `static` —
a primary-constructor parameter in the body is instance state, and CA1822 is
satisfied by that rather than waived. The call site inside `FetchAsync`
becomes `_tokenEndpoint ??= await DiscoverTokenEndpointAsync(client, ct);`,
unchanged, because the parameter is captured rather than passed:

```csharp
    private async Task<Uri> DiscoverTokenEndpointAsync(HttpClient client, CancellationToken ct)
```

```csharp
            throw new InvalidOperationException(
                $"The discovery document at '{client.BaseAddress}' declares no usable token_endpoint. " +
                $"'{authorityKey.Name}' names an OpenID provider (§11.3), and this " +
                "host needs that same one to mint its own token (§11.5).");
```

```csharp
    private string Unusable(HttpClient client, Uri endpoint, string fault) =>
        $"The discovery document at '{client.BaseAddress}' declares a token_endpoint of '{endpoint}', which " +
        $"{fault}. This host posts its client secret there (§11.5), so the endpoint may not be less protected " +
        $"than the authority '{authorityKey.Name}' names (§11.3).";
```

Every other line of the file, `Failure`'s reference to
`ServiceIdentityOptions.SectionName` included, is untouched: that type moved
with it.

- [ ] **Step 6: Rewrite `ServiceIdentityOptions`' remark**

It is the one moved file whose doc comment states something the move makes
false. Its current block runs twenty-two lines and carries `<b>`, so the edit
brings it under the comment gate's limit in the same change:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Common.Infrastructure.Identity;

/// <summary>
/// §15.4's options type for §11.5's client-credentials grant. It earns a
/// binding by holding a secret that differs per environment, which is
/// §15.4's test; each host that calls a peer binds it for itself (§15.3).
/// </summary>
/// <remarks>
/// <c>[Required]</c> is what makes <c>ValidateDataAnnotations</c> do
/// anything: an unbound options class resolves to a default instance, and a
/// bound one with no annotations validates while empty.
/// </remarks>
public sealed class ServiceIdentityOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Identity:Client";

    [Required] public string ClientId { get; init; } = "";

    [Required] public string ClientSecret { get; init; } = "";

    [Required] public string Scope { get; init; } = "";
}
```

**Do not write that §15.4 makes this the solution's only options type.**
ADR-053 gives Shipping's PR-6 the sentence that stops being true, and a copy
of the count here would be a second place to correct.

- [ ] **Step 7: Point the BFF at the building block**

PR-3a already gave `Common.Infrastructure` its `Microsoft.Extensions.Http`
reference; `Microsoft.Extensions.Options` and
`Microsoft.Extensions.Logging.Abstractions` were there before it, and the
annotations and JSON the moved types use ride in the base framework.

In `src/BFF/Web.Bff/Web.Bff.csproj`, in the `ProjectReference` group:

```xml
    <!-- §11.5's client-credentials grant, which ADR-052 puts in a building
         block because more than one host needs it. Every other host reaches
         this project through its own *.Infrastructure (§4.2); the BFF has
         none (§10.1), so it names it here and restores the outbox, inbox and
         Redis code it never calls — the cost Common.Web's EF Core reference
         already pays in the gateway, in the same currency. -->
    <ProjectReference Include="..\..\BuildingBlocks\Common.Infrastructure\Common.Infrastructure.csproj" />
```

In `src/BFF/Web.Bff/Dockerfile`, in the restore layer, before the
`Common.Web` line so the order matches the dependency order:

```dockerfile
COPY src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj src/BuildingBlocks/Common.Infrastructure/
```

The file's own comment already says why a missing line fails four steps later
with NETSDK1004, so add the line and nothing else. `ci.yml`'s `bff` filter
needs no change: its `shared` anchor already names
`src/BuildingBlocks/**`, which is what this image now copies more of.

- [ ] **Step 8: Re-point `Program.cs`**

`using Web.Bff.Identity;` becomes `using Common.Infrastructure.Identity;`,
which moves it above `using Common.Web;` in the sorted block. The comment over
the registrations says the mechanism lives here, and it no longer does:

```csharp
// §9.7, §11.5 — the client-credentials registrations, in the one host that
// makes them. The types are Common.Infrastructure.Identity's (ADR-052) and
// the binding is each host's own: "the gateway needs no client credentials"
// is true by CONSTRUCTION, because the gateway binds Identity:Client nowhere
// and therefore demands it nowhere (§15.4).
builder.Services.AddTransient<ClientCredentialsHandler>();
builder.Services.AddSingleton<ITokenCache, CachingTokenClient>();
```

and beside the line that already reads the authority, one registration:

```csharp
string authority = builder.Configuration[AuthenticationExtensions.AuthorityKey]!;

// The same key's NAME, carried into the token client because a building block
// below Common.Web cannot name it and a refused discovery document has to say
// which key to fix (§11.3, §11.5).
builder.Services.AddSingleton(new AuthorityKeyName(AuthenticationExtensions.AuthorityKey));
```

Everything else in `Program.cs` — the options binding, the named client, the
pricing pipeline's three statements and their ordering comments — is
untouched.

- [ ] **Step 9: Re-point the BFF's suite**

`using Web.Bff.Identity;` becomes `using Common.Infrastructure.Identity;` in
`BffFactory.cs`, `RecordingTokenCache.cs` and `OptionsValidationTests.cs`, and
in `CachingTokenClientTests.cs` and `TokenEndpointSchemeTests.cs`, which move
in Task 3 and have to compile here first. Those two build their own
`ServiceCollection`, so each registers the carrier — with a name of its own,
which is what makes the assertion about the mechanism rather than about a
constant it copied.

In `CachingTokenClientTests`, a field beside the scope and a line in
`InitializeAsync`:

```csharp
    private const string AuthorityKey = "Test:Authority";
```

```csharp
        services.AddSingleton(new AuthorityKeyName(AuthorityKey));
```

and the last test's assertion and its comment:

```csharp
        // The key the host was configured from is what turns this from
        // "something went wrong talking to the provider" into a deployment
        // instruction, so the name this client was given is what it prints.
        thrown.Message.ShouldContain(AuthorityKey);
```

In `TokenEndpointSchemeTests`, the same field and one line inside `Client`:

```csharp
    private const string AuthorityKey = "Test:Authority";
```

```csharp
        services.AddSingleton(new AuthorityKeyName(AuthorityKey));
```

Its three assertions are about the endpoint and the downgrade and do not move.

`OptionsValidationTests` stays in `Web.Bff.Tests` whole, and the reason is in
its own text: two of its three tests drive the BFF host through
`MissingSettingFactory`, and the third "duplicates `Program`'s four
registration lines on purpose", with the host theory named as what fails if
`Program` drops the block. Splitting the pair would leave each half proving
less than the pair does.

- [ ] **Step 10: Run**

```bash
dotnet build Platform.slnx
dotnet test tests/Web.Bff.Tests
dotnet test tests/Common.Infrastructure.Tests --filter "Category!=Integration"
```

Expected: the build is green with 0 warnings, and the BFF suite is green
including the two token suites, which are still in it. **That green is this
PR's proof that no behaviour moved**: the assertions have not changed, only
the namespace they compile against.

- [ ] **Step 11: Commit**

```bash
git add src/BuildingBlocks/Common.Infrastructure src/BFF/Web.Bff tests/Web.Bff.Tests
git commit -m "refactor(identity): the client-credentials grant moves to Common.Infrastructure"
```

---

### Task 3: The tests follow their types

**Files:**
- Move: `tests/Web.Bff.Tests/CachingTokenClientTests.cs` →
  `tests/Common.Infrastructure.Tests/CachingTokenClientTests.cs`
- Move: `tests/Web.Bff.Tests/TokenEndpointSchemeTests.cs` →
  `tests/Common.Infrastructure.Tests/TokenEndpointSchemeTests.cs`
- Move: `tests/Web.Bff.Tests/StubIdentityProvider.cs` →
  `tests/Common.Infrastructure.Tests/StubIdentityProvider.cs`
- Modify: `tests/Common.Infrastructure.Tests/Common.Infrastructure.Tests.csproj`
- Modify: `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`
- Modify: `.github/secret-scan/allowed/tests.txt`

**Interfaces:**
- Consumes: Task 2's `Common.Infrastructure.Identity` and `AuthorityKeyName`.

Which tests move, and which do not:

| Suite | Moves? | Why |
|---|---|---|
| `CachingTokenClientTests` | **moves** | its subject is `CachingTokenClient`, and its fixture is an in-process Kestrel host, not a container |
| `TokenEndpointSchemeTests` | **moves** | the same subject and the same fixture |
| `StubIdentityProvider` | **moves** | it is those two suites' fixture and nothing else uses it |
| `OptionsValidationTests` | stays | two of its three tests start the BFF host through `BffFactory`; the third is bound to them by its own argument |
| `RecordingTokenCache` | stays | an `ITokenCache` stand-in for the BFF host, used by `BffFactory` |
| `PricingCredentialsTests`, `ResilienceHierarchyTests`, `UpstreamRetryTests` | stay | their subject is the pricing hop, which is the BFF's |
| `KeycloakIdentityTests`, `RealmClientTests`, `KeycloakFixture` | **stay, and this is the container answer** | `KeycloakFixture` starts a real Keycloak (`Testcontainers.Keycloak`), and the two suites over it name none of the four moved types: their subject is the realm's audience mapper and its client list, which §4.1 puts in this suite and ADR-052's table marks **asserted** for the PR that adds Shipping's client. Nothing that moves is container-bound, so no test is left behind for want of a fixture |

- [ ] **Step 1: Move the three files**

```bash
git mv tests/Web.Bff.Tests/CachingTokenClientTests.cs \
       tests/Common.Infrastructure.Tests/CachingTokenClientTests.cs
git mv tests/Web.Bff.Tests/TokenEndpointSchemeTests.cs \
       tests/Common.Infrastructure.Tests/TokenEndpointSchemeTests.cs
git mv tests/Web.Bff.Tests/StubIdentityProvider.cs \
       tests/Common.Infrastructure.Tests/StubIdentityProvider.cs
```

In all three, `namespace Web.Bff.Tests;` becomes
`namespace Common.Infrastructure.Tests;`, and `using Common.Infrastructure.
Identity;` stays where Task 2 put it. No assertion, no fixture property and
no comment changes: the suites arrive able to fail for exactly what they
could fail for before.

- [ ] **Step 2: Give the suite the framework its fixture needs**

`StubIdentityProvider` builds a `WebApplication`, configures Kestrel, serves
`Results.Json` and reads an `IFormCollection`, and the two suites call
`AddHttpClient` and `AddLogging`. In
`tests/Common.Infrastructure.Tests/Common.Infrastructure.Tests.csproj`, above
the package group:

```xml
  <ItemGroup>
    <!-- §11.5's token source is proved against a real server, because half of
         what is under test is what the client does with a discovery document
         (the stub's own remarks). Kestrel and the minimal-API results are the
         shared framework's, and this suite is the only thing here that takes
         it — Common.Infrastructure itself does not. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

Nothing else: `Microsoft.Extensions.TimeProvider.Testing`,
`Microsoft.Extensions.DependencyInjection`, `Shouldly` and the runner are
already referenced, and `Microsoft.Extensions.Http` arrives with the framework
reference.

- [ ] **Step 3: Drop what the BFF's suite no longer uses**

In `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`, remove the
`Microsoft.Extensions.TimeProvider.Testing` reference and the comment over it:
`FakeTimeProvider` was named by `CachingTokenClientTests` alone, and an unused
reference is a claim about the dependency graph that nothing makes true.

Prove it rather than assume it:

```bash
grep -rn "FakeTimeProvider\|Time.Testing" tests/Web.Bff.Tests
```

Expected: no output. `Microsoft.Extensions.Http.Resilience`,
`Testcontainers.Keycloak` and `System.IdentityModel.Tokens.Jwt` all stay —
`ResilienceHierarchyTests` and the two Keycloak suites still name them.

- [ ] **Step 4: Re-path the accepted findings**

The secret scan keys an accepted finding by path and **fails the build on an
entry that matches no finding**, so four lines in
`.github/secret-scan/allowed/tests.txt` change path and nothing else — the
fingerprint is of the literal, which is why the same `eb26d4a9a0ef` already
appears against four different files:

```
tests/Common.Infrastructure.Tests/CachingTokenClientTests.cs | credential-assignment | eb26d4a9a0ef | Section 11.5's local BFF client default, bound in a test fixture.
tests/Common.Infrastructure.Tests/CachingTokenClientTests.cs | credential-assignment | ca2a7d5ccd7e | An invented bearer value in a stub error body, asserted never to be logged.
tests/Common.Infrastructure.Tests/TokenEndpointSchemeTests.cs | credential-assignment | eb26d4a9a0ef | The same local BFF client default in the scheme suite.
tests/Common.Infrastructure.Tests/TokenEndpointSchemeTests.cs | credential-assignment | 96b2735306f5 | A non-HTTP endpoint URL asserted to be refused. A URL, not a credential.
```

They keep their position in the file's hand-built block, which is ordered by
tree. The two `tests/Web.Bff.Tests/KeycloakIdentityTests.cs` entries stay
exactly where they are.

- [ ] **Step 5: Run both suites and the scan**

```bash
dotnet test tests/Common.Infrastructure.Tests --filter "Category!=Integration"
dotnet test tests/Web.Bff.Tests
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: both suites green and both Python commands exit 0. If the scan
reports a fingerprint this plan does not print, take the gate's value and not
this one; a digest written from a document is a digest with two owners.

- [ ] **Step 6: Commit**

```bash
git add tests/Common.Infrastructure.Tests tests/Web.Bff.Tests .github/secret-scan/allowed/tests.txt
git commit -m "refactor(identity): the token-source suites move to Common.Infrastructure.Tests"
```

---

### Task 4: The two remarks that move with the types

**Files:**
- Modify: `src/BuildingBlocks/Common.Web/ServiceOptions.cs`
- Modify: `docs/backend-architecture/04-solution-structure.md`

Spec section 13 gives PR-3 exactly these two: §4.1's tree comment and
`ServiceOptions`' remark, both about the one host with client credentials.
Nothing else in the corpus moves here, and section 13 says which PR each of
the others is.

- [ ] **Step 1: `ServiceOptions`' remark**

Its class doc names `Web.Bff` as where the one options type lives, which the
move makes wrong. The block is twenty lines and carries `<b>`, so it comes
under the comment gate's limit in the same edit. `OperationTimeout`'s own
doc below it is untouched and stays as long as it is:

```csharp
namespace Common.Web;

/// <summary>
/// §15.4's static constants — the tier of settings that are genuinely not
/// configuration, and are therefore not bound, not validated and not
/// deployable. §15.4's test is whether a member would differ between
/// Compose, the fixture and production; nothing here does, and
/// <c>ServiceIdentityOptions</c> is the type that passes it (§11.5).
/// </summary>
/// <remarks>
/// It caps a hierarchy that is the platform's and not one host's (§9.7).
/// </remarks>
public static class ServiceOptions
```

The sentence about a binding hoisted into `Common.Web` re-imposing a
credential on every host is not lost: `ServiceIdentityOptions`' own remark
carries it, one file over, where the binding it argues about is.

- [ ] **Step 2: §4.1's tree comment**

Two entries in the tree change. `Common.Infrastructure` gains the mechanism:

```
│   │   ├── Common.Infrastructure/      Outbox, inbox, idempotency markers,
│   │   │                               EF conventions, Redis, and §11.5's
│   │   │                               client-credentials grant (ADR-052)
```

and the BFF keeps the hop and loses the claim that the code is its:

```
│   └── Web.Bff/                        Aggregation for the web client (§10.1).
│                                       The ONLY host that calls a service
│                                       synchronously (§9.7); it binds
│                                       Identity:Client, and the grant's code is
│                                       Common.Infrastructure's (§11.5, ADR-052)
```

The `tests/Web.Bff.Tests/` entry is left alone: it says "§9.7's hop and
§11.5's credentials", and after this PR that suite still holds
`OptionsValidationTests`, `PricingCredentialsTests` and the one real Keycloak,
so the sentence is true as it stands.

- [ ] **Step 3: Leave the rest**

ADR-052's consequences table lists every place that says the BFF is alone, and
assigns them to "the pull request that builds each service" —
`docs/repo-map.md`, `docs/secrets.md`, §9.7, §11.5, §14.1, §15.1, §15.4, the
Helm helper and `smoke.sh` among them. None is in this PR's touch set, and
`docs/change-locality.md` is explicit about a stale restatement met in
passing: leave it. In particular **§15.4's sentence that the solution has one
options type is ADR-053's and moves in Shipping's PR-6**, not here.

Two of those are worth a check rather than a change, because they read the
tree and would go red if this PR had moved the wrong thing:

```bash
grep -rq 'ServiceIdentityOptions' src/BFF/Web.Bff && echo "smoke.sh's predicate still holds"
grep -rn "Web.Bff.Identity" . --include="*.cs" --include="*.md" --include="*.sh" --include="*.yaml"
```

Expected: the first prints its message — `Program.cs` binds the options type
and `deploy/helm/smoke.sh` requires exactly that — and the second prints
nothing. `tools/new-service` needs no change at all: it names no `Identity`
path and renders no host that holds one.

- [ ] **Step 4: Run the document checks**

```bash
py -3.12 .github/licence-gate/licence_gate.py
```

then `/check-links` and `/validate-blueprint`, because a chapter changed.

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Common.Web/ServiceOptions.cs \
        docs/backend-architecture/04-solution-structure.md
git commit -m "refactor(identity): the remark and the tree entry follow the grant"
```

---

### Task 5: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings. `TreatWarningsAsErrors` makes
  that the build's own claim, and the one thing to read by eye is that no
  `#pragma` was added anywhere.
- [ ] `dotnet test Platform.slnx` — green, with a Docker daemon running: the
  Keycloak, SQL Server, RabbitMQ and Redis suites are `Category=Integration`
  and are never skipped.
- [ ] `py -3.12 -m unittest discover -s .github/licence-gate` and
  `py -3.12 .github/licence-gate/licence_gate.py` — both exit 0.
- [ ] `py -3.12 -m unittest discover -s .github/secret-scan` and
  `py -3.12 .github/secret-scan/secret_scan.py` — both exit 0.
- [ ] `git fetch origin main` then
  `py -3.12 .github/comment-gate/comment_gate.py --base origin/main` — exit 0.
  The two rewritten doc comments are the blocks it judges; every other block
  in the moved files is unchanged and a renamed file is judged on its changed
  lines alone.
- [ ] `dotnet restore Platform.slnx`, `dotnet build Platform.slnx`, then
  `py -3.12 .github/output-gate/output_gate.py` — exit 0, because the BFF's
  publish output gained a building block's assemblies.
- [ ] `docker build -f src/BFF/Web.Bff/Dockerfile .` — the restore layer's new
  `COPY` is what this proves, and NETSDK1004 naming `Common.Infrastructure` is
  what a missing line looks like.
- [ ] PR body: `| Class | B+D+E |` and the touch set from the Global
  Constraints. The body says what the class row cannot: that the locality
  gate admits no such row today, and names the Class D change that has to land
  first. Then `/ship`.

## Self-review

**Spec coverage.**

- Section 2's bullet — `ITokenCache`, `CachingTokenClient`,
  `ClientCredentialsHandler` and `ServiceIdentityOptions` move to
  `Common.Infrastructure` with their tests → Tasks 2 and 3, with the table in
  each naming what moves and what stays and why.
- Section 2's second sentence — `ServiceOptions`' remark and §4.1's tree
  comment move with them → Task 4.
- Section 3's PR-3 row — the four types and their tests out of `Web.Bff`, the
  BFF re-pointed, no behaviour moved → Tasks 2 and 3; the "no behaviour"
  claim is discharged by Task 2 step 11, which runs the moved suites
  unchanged in their old home before they move.
- Section 3's order — 3 → 4, touching no Shipping path → the touch set names
  none, and the Global Constraints say the PR depends on nothing.
- Section 13's PR-3 line → Task 4, and its PR-6 line is why Task 4 step 3
  refuses §15.4's sentence.
- ADR-052's consequence that the identity types move to a building block →
  Task 2; its list of everything else that says the BFF is alone → Task 4
  step 3, left to the pull requests that record names.

**Type consistency.** `ITokenCache`, `CachingTokenClient.HttpClientName`,
`ClientCredentialsHandler`, `ServiceIdentityOptions.SectionName` and
`AuthorityKeyName(string Name)` are produced by Task 2 under
`Common.Infrastructure.Identity` and consumed under those spellings by Task
2's `Program.cs` and `BffFactory`, by Task 3's two moved suites, and by
Shipping's PR-5, which registers the handler inside the address client's
resilience pipeline. `AuthenticationExtensions.AuthorityKey` and
`AuthenticationExtensions.Audience` stay `Common.Web`'s and are named only
from `Web.Bff` and from test projects.

**Left to a later PR.**

- **Any second host binding `Identity:Client`.** Shipping's `shipping-worker`
  client, its secret and the three `Identity__Client__*` keys are PR-4's and
  PR-5's; this PR adds no host and no key.
- **A registration extension over the three lines `Program.cs` holds.** PR-5
  is the first pull request with a second caller and therefore the first that
  can tell a shared registration from a copied one; hoisting one here would
  be a guess at what the second host needs.
- **The token client holding its own `permission` claim** to
  `orders:delivery-address`, which ADR-052 decides and PR-5 builds. Nothing
  in the moved code inspects a token it was issued.
- **§15.4's "only options type" sentence and its callout**, ADR-053's, moved
  by PR-6 when `ShippingJurisdictionOptions` becomes the second.
- **Every other sentence in ADR-052's table** — §9.7, §11.5, §14.1, §15.1,
  `docs/repo-map.md`, `docs/secrets.md`, the Helm helper, `smoke.sh` and
  `RealmClientTests` — each owed by the pull request that makes it false.
