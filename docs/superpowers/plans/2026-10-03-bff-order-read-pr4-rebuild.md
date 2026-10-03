# BFF order read PR-4 — the order projection's rebuild — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give ADR-051's projection the rebuild its trap asks for on the
first day. `tools/bff-replay` is a .NET console that opens the BFF's
database, the broker under the BFF's own account and one read-only
connection per publisher; refuses to start with any of them missing or
unreachable; with `--reset` deletes the projection's order rows and the
queue's inbox rows in one transaction; reads each publisher's processed
`Broker`-lane outbox rows of its share of the eight events, oldest first;
sends each to `queue:bff-order-events` alone with the message id,
correlation id and `OccurredAt` the row holds; and prints what it sent and
how far back each publisher's window reached. Its procedure is its README.
The output gate learns that `tools/` now holds a project the solution
builds, §10.7 and a callout on ADR-051 say the window is the publishers'
outboxes rather than the broker's, and §4.1's tree, the repo map and
`CLAUDE.md`'s tree gain the entry.

**Architecture:** the tool reads the outbox through the code that wrote it —
`MessageTypeMap` over `Common.Contracts` resolves `MessageType`, a converter-
free `OutboxJson` reads `Payload`, `OutboxTable` delimits each publisher's
table, and the column names are `nameof(OutboxMessage.…)`, so a renamed
column is a compile error rather than an empty replay. It references
`Common.Infrastructure` (which carries `Common.Contracts`) and
`Web.Bff.Persistence`, for `BffSchema.Name`, and nothing of any service.
**The credential is the guard**: the broker connection is the BFF's own
`bff-svc`, whose grant writes `bff-` exchanges and no `Common.Contracts`
exchange, so a publish to a contract's exchange — the redelivery to every
other consumer section 8 forbids — is refused by the broker rather than
avoided by the tool's discipline. Every source is opened and probed, and the
broker answers, before the reset runs, so a reset never precedes a read that
cannot happen. Its suite is `Web.Bff.Tests`, the BFF's one suite (spec,
section 11), because the proof is the consumers' own projection rebuilt from
the same events.

**Tech Stack:** .NET at `global.json`'s pin; MassTransit.RabbitMQ, Dapper and
Microsoft.Data.SqlClient, all pinned and listed in Appendix B today; xUnit v3
with Shouldly over `BffServiceFixture`'s SQL Server and RabbitMQ containers;
stdlib Python 3.12 for the output gate and its suite.

**Spec:** `docs/superpowers/specs/2026-10-03-bff-order-read-design.md`,
sections 1 (*The rebuild, and what it reads*), 3 (the rebuild tool, its suite
and its procedure), 4 (PR-4's row, and that it follows PR-2), 8 (the tool's
six steps and the window), 11 (the rebuild's testing line) and 12 (§10.7's
sentence and ADR-051's callout, both PR-4's).

**Four spec decisions with consequences in the tree, each argued where it
lands:**

1. **The procedure is `tools/bff-replay/README.md`, not a file under
   `docs/runbooks/`** (spec section 8). That directory is one runbook per alert, both ways:
   `deploy/observability/check.py` fails a runbook no alert names, and its
   `NOT_A_RUNBOOK` admits `README.md` alone (`check.py:67-69`,
   `docs/runbooks/README.md`'s first line). PR-4 adds no alert, so a runbook
   here would fail the gate; the procedure sits beside its tool, as
   `tools/new-service/README.md` does, and PR-5's runbook for the
   unattributed-order rule points at it (*Interfaces for PR-5*).
2. **The output gate walks `tools/`** (spec sections 4 and 8). A `.csproj` under `tools/` that
   `Platform.slnx` lists fails it twice — the subject reconciliation reports
   the listed project as one "the walk over src/ or tests/ did not find"
   (`output_gate.py:123-130`), and `ThisRepository` fails any solution entry
   outside `SOURCE_ROOTS` (`test_output_gate.py:415-424`) — and leaving the
   tool out of the solution would put it outside `dotnet format`'s and that
   gate's reach, the silent-coverage failure `CLAUDE.md` names. The gate's
   own comment gives the reason `tools/` was absent — the scaffold restores
   nothing — and that reason is gone. So the gate, its suite, its README,
   `ci.yml`'s byte-identical step and §4.1's two sentences name the third
   tree.
3. **`--reset` keeps `bff.Products`** (spec section 8, step 3). A product's
   name is published once, usually long before
   any outbox window, so deleting the table loses every name Catalog's
   window no longer holds, permanently, and the history then reads
   `productName: null` for products that had one. The upsert guards on
   `OccurredAt`, so a replayed `ProductPublished` over a kept row is harmless.
   The README says how to clear the table when it is the table that is wrong.
4. **The preflight is section 8's step 2**, before the reset in step 3: the
   tool opens and probes all four outboxes and waits for the broker to
   answer before it deletes anything.

**Not run before it was written.** Every anchor quoted below was read from
the tree at `cc453404`, and PR-1's and PR-2's names from their plans. The
code is written against both and has not been compiled; each task's run
steps are the first proof, and a divergence they find is fixed in the task,
not carried.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set:

  `tools/bff-replay/**`, `tests/Web.Bff.Tests/**`, `Platform.slnx`, `.github/output-gate/**`, `.github/workflows/ci.yml`, `docs/backend-architecture/04-solution-structure.md`, `docs/backend-architecture/10-api-gateway.md`, `docs/backend-architecture/adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md`, `docs/repo-map.md`, `CLAUDE.md`

  Why each, since the row is paths only. **A**: the BFF's one suite, which
  gains the tool's tests, the fixture's two members and the publishers'
  outbox harness. **D**: the tool and its README; the output gate, its suite
  and README, and the CI step that names the same trees (decision 2); §4.1's
  tree entry and two sentences; §10.7's sentence and ADR-051's callout (spec,
  section 12); the repo map's and `CLAUDE.md`'s trees, each one line per
  entry and each false without it. **E**: `Platform.slnx` and the tool's
  `.csproj`, and `Web.Bff.Tests.csproj`'s references.
- **Mutexes held**: `Platform.slnx` from the repo-wide list. No service
  mutex: the tool touches no `Program.cs`, no `DbContext`, no migration.
  `Directory.Packages.props` is **not** touched — every package named below
  is pinned — so no Appendix B row moves. The Dapper row lists the
  package's uses; the tool is one more, and that row is an inventory the
  contract's section 2 says not to extend, so it is left as it reads.
- **Depends on PR-2 having merged**, and therefore on PR-1. Task 1 reads
  each name below on disk and stops on a miss rather than guessing:
  - PR-1: `src/BFF/Web.Bff.Persistence` with `Web.Bff.Persistence.BffSchema.Name`
    = `"bff"`; tables `bff.Orders`, `bff.OrderLines` (cascading
    `FK_OrderLines_Orders_OrderId`), `bff.Products`, `bff.InboxMessages`;
    `BffServiceFixture` and `BffIntegrationCollection` in
    `tests/Web.Bff.Tests`, deriving `Common.TestSupport.ServiceFixture` under
    the name `Bff`, so its broker account is `bff-svc`.
  - PR-2: `Web.Bff.Messaging.DependencyInjection.EventsQueue` =
    `"bff-order-events"`; the account `bff-svc` with `write`
    `^(bff-|MassTransit:)`; `MessagingRegistrationTests.Consumed`, the eight
    types; the fixture's `DeliverAsync<T>`, `WaitUntilAsync`, `OrderAsync`,
    `LinesAsync`; `ProjectedOrder` with `Facts()`, `ProjectedLine`,
    `OrderEvents` and its builders (`Lamp`, `Currency`, `TrackingNumber`,
    `Placed`, `Confirmed`, `Cancelled`, `Authorised`, `Refunded`,
    `Dispatched`, `Delivered`, `Published`); `BffFactory.UnreachableBroker`;
    the chart's broker Secret `web-bff-rabbitmq`, key `connection-string`.
- Comments say why and cite the owner; no history, no PR, no test named. The
  comment gate's `BLOCK_LIMIT` is 5 and reaches Python docstrings, YAML and
  `.csproj` comments as well as C#; a touched block counts whole, and a file
  the branch creates is all added lines. A summary is one sentence, a
  `<remarks>` cited and four lines. Prose at 80 columns, code at 120, British
  spelling, explicit local types (the four carve-outs), file-scoped
  namespaces, braces on two statements or more, one space before `=`, `=>`
  and `{`.
- **No literal credential in this plan or in the README.** The secret scan
  reads `docs/superpowers/` and `tools/`; the README names which key holds a
  connection and where its value comes from, and prints none.
- `py -3.12`, never `python`. Container tests are
  `[Collection(nameof(BffIntegrationCollection))]` and never skipped.
- Every step that adds behaviour writes its test first, and a test whose
  subject is a refusal asserts the refusal's own message or number.
- Branch: `feat/bff-order-projection-rebuild`.

---

### Task 1: PR-1's and PR-2's names, verified on disk

**Files:** none.

- [ ] **Step 1: Read every name this plan consumes**

```bash
rg -n "public const string Name = \"bff\"" src/BFF/Web.Bff.Persistence
rg -n "EventsQueue = \"bff-order-events\"" src/BFF/Web.Bff
rg -n "\"user\": \"bff-svc\"" -A 4 deploy/compose/rabbitmq/definitions.json
rg -n "class BffServiceFixture|class BffIntegrationCollection|DeliverAsync|WaitUntilAsync|OrderAsync|LinesAsync" tests/Web.Bff.Tests
rg -n "Consumed|UnreachableBroker|record ProjectedOrder|Facts\(\)|class OrderEvents" tests/Web.Bff.Tests
rg -n "OnDelete|Cascade" src/BFF/Web.Bff.Persistence
```

Expected: each pattern matches. `write` reads `^(bff-|MassTransit:)`; the
`OrderLines` configuration cascades on its foreign key. A miss is a defect
in the merged PR, raised there; this plan does not paper over it.

---

### Task 2: The output gate walks `tools/`

**Files:**
- Modify: `.github/output-gate/output_gate.py`
- Modify: `.github/output-gate/test_output_gate.py`
- Modify: `.github/output-gate/README.md`
- Modify: `.github/workflows/ci.yml` (the byte-identical step)
- Modify: `docs/backend-architecture/04-solution-structure.md` (§4.1's two sentences)

- [ ] **Step 1: Write the failing gate tests**

In `.github/output-gate/test_output_gate.py`, add to class `Residue`, after
`test_a_bin_under_tests_fails`:

```python
    def test_an_obj_beside_a_project_under_tools_fails(self) -> None:
        """`tools/` holds a project the solution builds, so it is source too."""
        tree(self.root, {"BffReplay": "tools/bff-replay"})
        (self.root / "tools/bff-replay/obj").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("tools/bff-replay/obj/", output)
```

and to class `CleanTree`, after its first test:

```python
    def test_a_project_under_tools_in_the_solution_is_walked(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "BffReplay": "tools/bff-replay"})

        code, output = run(self.root)

        self.assertEqual(code, 0)
        self.assertIn("2 project(s) under src/, tests/ and tools/", output)
```

and in `Subject.test_a_project_moved_without_the_solution_following_it_fails`
(line 211), the expected phrase becomes the three roots:

```python
        self.assertIn("the walk over src/, tests/ or tools/ did not find", output)
```

`tree()` creates every directory it is given, so the fixtures need no
`tools/` in `setUp`, and every other test then runs with that root absent —
which is the case a checkout without the tool would be in.

- [ ] **Step 2: Run them**

```bash
py -3.12 -m unittest discover -s .github/output-gate
```

Expected: 3 failures — the `obj/` under `tools/` is not reported, the
listed `tools/` project is called one the walk "did not find", and the
moved-project message still reads `src/ or tests/`.

- [ ] **Step 3: The gate names the third tree**

In `.github/output-gate/output_gate.py`, replace the `SOURCE_ROOTS` comment
and tuple:

```python
# The trees Section 4.1 names. `tools/` is one because it holds a project the
# solution builds, and a project outside the walk is reconciled against nothing.
SOURCE_ROOTS = ("src", "tests", "tools")


def spoken(conjunction: str) -> str:
    """The roots as a sentence lists them: `src/, tests/ and tools/`."""
    names = [f"{name}/" for name in SOURCE_ROOTS]
    return f"{', '.join(names[:-1])} {conjunction} {names[-1]}"
```

Then each of the three joins uses it: line 116's
`roots = " and ".join(f"{name}/" for name in SOURCE_ROOTS)` becomes
`roots = spoken("and")`, line 123's `" or ".join(...)` becomes
`roots = spoken("or")`, and line 273's becomes `roots = spoken("and")`. The
failure footer at line 268 becomes:

```python
        print(f"\nSection 4.1: {spoken('and')} hold source, and nothing a build wrote. "
              "Directory.Build.props owns where output goes; see its Output comment.")
```

and the success line's `neither tree holds` becomes `no tree holds`, since
there are three. The module docstring is left as it reads: it says what
`src/` and `tests/` hold, which stays true, and a touched docstring counts
whole against the comment gate's limit.

- [ ] **Step 4: Run the suite again**

```bash
py -3.12 -m unittest discover -s .github/output-gate
```

Expected: every test passes. `ThisRepository` still passes because nothing
under `tools/` is in the solution yet; Task 3 lists the tool and that class
is then the assertion that notices.

- [ ] **Step 5: The README's claim, the CI step and §4.1**

`.github/output-gate/README.md`, the claim's first sentence:

```markdown
**The claim: no `bin/` or `obj/` exists under `src/`, `tests/` or `tools/`,
and every project's output is under `artifacts/` instead.** It checks
```

and the first bullet of *What it reads*:

```markdown
- The directory trees under `src/`, `tests/` and `tools/`, for a `bin/` or
  `obj/` anywhere beneath any of them.
```

`.github/workflows/ci.yml`, in the `build` job, the step that follows the
output gate — its name and its one command, and no comment above it:

```yaml
      - name: The build left src/, tests/ and tools/ byte-identical
        run: |
          wrote="$(git status --porcelain --ignored -- src tests tools)"
```

leaving the `if`/`echo`/`exit` lines below it as they are.

`docs/backend-architecture/04-solution-structure.md`, §4.1, the paragraph
that opens `**\`src/\` and \`tests/\` hold source, and nothing a build
wrote.**` becomes:

```markdown
**`src/`, `tests/` and `tools/` hold source, and nothing a build wrote.** A
project directory carrying its own `bin/` and `obj/` buries the files a reader
came for under the ones a build wrote, and every gate that walks a source tree
pays for it again in a skip list that silently decides what the gate reads.
```

and the paragraph that opens `**That \`src/\` and \`tests/\` hold nothing a
build wrote`:

```markdown
**That `src/`, `tests/` and `tools/` hold nothing a build wrote is checked
rather than asserted**, by
[`.github/output-gate/`](../../.github/output-gate/README.md), whose README
owns what it reads and which half of this sentence it leaves to a CI step.
```

The tree's `artifacts/` entry and the new `tools/bff-replay/` entry are
Task 7's, with the rest of the chapter work.

- [ ] **Step 6: Commit**

```bash
git add .github/output-gate .github/workflows/ci.yml docs/backend-architecture/04-solution-structure.md
git commit -m "chore(output-gate): walk tools/, which now holds a project the solution builds"
```

The body argues decision 2: the tool is listed in the solution so that the
format check and this gate reach it, and the gate's stated reason for
omitting `tools/` — a scaffold that restores nothing — no longer describes
the directory.

---

### Task 3: The tool's command line and its settings

**Files:**
- Create: `tools/bff-replay/BffReplay.csproj`
- Create: `tools/bff-replay/EntryPoint.cs`
- Create: `tools/bff-replay/ReplayCommand.cs`
- Create: `tools/bff-replay/ReplaySettings.cs`
- Create: `tools/bff-replay/ReplaySettingsException.cs`
- Create: `tools/bff-replay/Publisher.cs`
- Modify: `Platform.slnx`
- Modify: `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`
- Create: `tests/Web.Bff.Tests/ReplayCommandTests.cs`

- [ ] **Step 1: The project, so the suite can reference it**

`tools/bff-replay/BffReplay.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- ADR-051's rebuild, an operator's console: the publishers' outbox rows, sent to the BFF's queue alone. -->

  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <!-- The send to bff-order-events; Common.Infrastructure carries MassTransit's core and no transport. -->
    <PackageReference Include="MassTransit.RabbitMQ" />
    <!-- The outbox reads and the reset, named directly, as every caller of either names them. -->
    <PackageReference Include="Dapper" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
  </ItemGroup>

  <ItemGroup>
    <!-- MessageTypeMap, OutboxJson and the table types: the code that wrote the rows reads them back (§9.4). -->
    <ProjectReference Include="..\..\src\BuildingBlocks\Common.Infrastructure\Common.Infrastructure.csproj" />
    <!-- BffSchema.Name, so the reset deletes from the schema its owner declares. -->
    <ProjectReference Include="..\..\src\BFF\Web.Bff.Persistence\Web.Bff.Persistence.csproj" />
  </ItemGroup>

</Project>
```

`Platform.slnx`, after the `/tests/` folder's closing tag and before
`</Solution>`:

```xml
  <Folder Name="/tools/">
    <Project Path="tools/bff-replay/BffReplay.csproj" />
  </Folder>
```

The scaffold's `update_solution` splices service folders and `tests/` entries
by pattern (`tools/new-service/scaffold/render.py:839-868`), so a folder
after `/tests/` is outside both patterns and moves nothing it renders.

`tests/Web.Bff.Tests/Web.Bff.Tests.csproj`, in the package group:

```xml
    <!-- The publishers' outbox harness stages rows through these, named directly though Web.Bff carries both. -->
    <PackageReference Include="Dapper" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
```

and in the project group:

```xml
    <!-- ADR-051's rebuild, proved against the consumers it feeds, which is why its suite is this one. -->
    <ProjectReference Include="..\..\tools\bff-replay\BffReplay.csproj" />
```

- [ ] **Step 2: Write the failing command tests**

`tests/Web.Bff.Tests/ReplayCommandTests.cs`:

```csharp
using BffReplay;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The rebuild's refusals, each made before anything is opened, so none needs a container.</summary>
public sealed class ReplayCommandTests
{
    [Fact]
    public async Task An_argument_other_than_reset_is_refused_before_the_environment_is_read()
    {
        int reads = 0;
        StringWriter error = new();

        int code = await ReplayCommand.RunAsync(
            ["--rest"],
            _ =>
            {
                reads++;
                return "set";
            },
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        code.ShouldBe(ReplayCommand.Refused);
        error.ToString().ShouldContain("Usage: bff-replay [--reset]");
        reads.ShouldBe(0);
    }

    [Fact]
    public async Task Reset_beside_a_second_argument_is_refused_too()
    {
        StringWriter error = new();

        int code = await ReplayCommand.RunAsync(
            [ReplayCommand.ResetFlag, "now"],
            _ => "set",
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        code.ShouldBe(ReplayCommand.Refused);
        error.ToString().ShouldContain("Usage: bff-replay [--reset]");
    }

    [Fact]
    public async Task Every_missing_connection_is_named_in_one_refusal()
    {
        StringWriter error = new();

        int code = await ReplayCommand.RunAsync(
            [ReplayCommand.ResetFlag],
            _ => null,
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        code.ShouldBe(ReplayCommand.Refused);
        foreach (string key in ReplaySettings.Keys)
            error.ToString().ShouldContain(key);
        error.ToString().ShouldContain("Nothing was read, deleted or sent");
    }

    [Fact]
    public async Task A_blank_connection_is_missing_rather_than_a_string_to_dial()
    {
        Dictionary<string, string> given = ReplaySettings.Keys.ToDictionary(key => key, _ => "Server=somewhere");
        given[ReplaySettings.BrokerKey] = "   ";
        StringWriter error = new();

        int code = await ReplayCommand.RunAsync(
            [],
            key => given[key],
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        code.ShouldBe(ReplayCommand.Refused);
        error.ToString().ShouldContain(ReplaySettings.BrokerKey);
        error.ToString().ShouldNotContain(ReplaySettings.BffKey);
    }

    [Fact]
    public void Every_key_given_reads_into_one_connection_per_publisher()
    {
        ReplaySettings settings = ReplaySettings.FromEnvironment(key => $"value-of-{key}");

        settings.Bff.ShouldBe($"value-of-{ReplaySettings.BffKey}");
        settings.Broker.ShouldBe($"value-of-{ReplaySettings.BrokerKey}");
        settings.Publishers.Select(p => p.Publisher).ShouldBe(Publisher.All);
        settings.Publishers.ShouldAllBe(p => p.ConnectionString == $"value-of-{p.Publisher.Key}");
    }
}
```

- [ ] **Step 3: Run them**

```bash
dotnet build Platform.slnx
```

Expected: the build fails with CS0246 for `ReplayCommand`, `ReplaySettings`
and `Publisher` in `ReplayCommandTests.cs`, and the tool project fails CS5001
(no static `Main`).

- [ ] **Step 4: The settings, the publishers and the refusal**

`tools/bff-replay/Publisher.cs`:

```csharp
using Common.Infrastructure.Outbox;

namespace BffReplay;

/// <summary>A service whose outbox holds some of ADR-051's eight events, read by an operator's login.</summary>
public sealed record Publisher(string Name, string Schema)
{
    /// <summary>The four, each by the schema its service's <c>OutboxMessageConfiguration</c> maps.</summary>
    public static readonly IReadOnlyList<Publisher> All =
    [
        new("Catalog", "catalog"),
        new("Ordering", "ordering"),
        new("Payments", "payments"),
        new("Shipping", "shipping")
    ];

    /// <summary>The environment key its read-only connection is taken from.</summary>
    public string Key => $"ConnectionStrings__{Name}Outbox";

    /// <summary>The namespace its contracts live in, which selects its share of the eight (§9.2).</summary>
    public string ContractNamespace => $"Common.Contracts.{Name}.V1";

    /// <summary>Its outbox, delimited by the type every service registers its own with (§9.4).</summary>
    public string Outbox => new OutboxTable(Schema).QualifiedName;
}

/// <summary>A publisher beside the connection string that reads its outbox.</summary>
public sealed record PublisherConnection(Publisher Publisher, string ConnectionString);
```

`tools/bff-replay/ReplaySettings.cs`:

```csharp
namespace BffReplay;

/// <summary>The six connections a replay takes, all of them or none.</summary>
public sealed record ReplaySettings(string Bff, string Broker, IReadOnlyList<PublisherConnection> Publishers)
{
    /// <summary>The BFF's runtime key (§7.1), whose rows the reset deletes.</summary>
    public const string BffKey = "ConnectionStrings__Bff";

    /// <summary>The bus, under <c>bff-svc</c>, whose grant writes no contract exchange (ADR-036).</summary>
    public const string BrokerKey = "ConnectionStrings__RabbitMq";

    /// <summary>Every key a replay reads, in the order a refusal names them.</summary>
    public static IReadOnlyList<string> Keys => [BffKey, BrokerKey, .. Publisher.All.Select(p => p.Key)];

    /// <summary>Reads every key, and refuses with every missing one named rather than the first.</summary>
    public static ReplaySettings FromEnvironment(Func<string, string?> environment)
    {
        string[] missing = [.. Keys.Where(key => string.IsNullOrWhiteSpace(environment(key)))];
        if (missing.Length > 0)
            throw new ReplaySettingsException(missing);

        return new ReplaySettings(
            environment(BffKey)!,
            environment(BrokerKey)!,
            [.. Publisher.All.Select(p => new PublisherConnection(p, environment(p.Key)!))]);
    }
}
```

`tools/bff-replay/ReplaySettingsException.cs`, with the three standard
constructors CA1032 asks for, as `ContactSourceRefusedException` has them:

```csharp
namespace BffReplay;

/// <summary>A replay refused before it opens anything, naming every key it was not given.</summary>
public sealed class ReplaySettingsException : Exception
{
    public ReplaySettingsException()
    {
        Missing = [];
    }

    public ReplaySettingsException(string message)
        : base(message)
    {
        Missing = [];
    }

    public ReplaySettingsException(string message, Exception innerException)
        : base(message, innerException)
    {
        Missing = [];
    }

    public ReplaySettingsException(IReadOnlyList<string> missing)
        : base(
            $"bff-replay needs {string.Join(", ", missing)} and was not given them. Nothing was read, " +
            "deleted or sent; tools/bff-replay/README.md says which login each one is.")
    {
        Missing = missing;
    }

    /// <summary>The keys absent or blank, all of them, so one run names every fix.</summary>
    public IReadOnlyList<string> Missing { get; }
}
```

- [ ] **Step 5: The command and the entry point**

`tools/bff-replay/ReplayCommand.cs`:

```csharp
namespace BffReplay;

/// <summary>The command line: no argument repairs, <c>--reset</c> rebuilds, and anything else is refused.</summary>
public static class ReplayCommand
{
    /// <summary>The replay ran to the end. A run that fails throws, and the process exits non-zero on it.</summary>
    public const int Succeeded = 0;

    /// <summary>Refused before anything was opened: a bad argument or a missing connection.</summary>
    public const int Refused = 2;

    public const string ResetFlag = "--reset";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<string, string?> environment,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (args.Count > 1 || (args.Count == 1 && args[0] != ResetFlag))
        {
            await error.WriteLineAsync(
                $"Usage: bff-replay [{ResetFlag}]. Without it the replay repairs over the projection; with it " +
                "the order rows and the queue's inbox rows go first (tools/bff-replay/README.md).");
            return Refused;
        }

        ReplaySettings settings;
        try
        {
            settings = ReplaySettings.FromEnvironment(environment);
        }
        catch (ReplaySettingsException refused)
        {
            await error.WriteLineAsync(refused.Message);
            return Refused;
        }

        ReplayReport report = await Replay.RunAsync(settings, args.Count == 1, output, Replay.BrokerDeadline, ct);
        await output.WriteLineAsync($"Sent {report.SentMessageIds.Count} event(s) to {Replay.Queue}.");
        return Succeeded;
    }
}
```

`tools/bff-replay/EntryPoint.cs`:

```csharp
namespace BffReplay;

/// <summary>The process: the real environment and console, and nothing the tests cannot reach without them.</summary>
/// <remarks>
/// Not top-level statements: those compile to a global <c>Program</c>, which <c>Web.Bff.Tests</c> already names as
/// the BFF's own for <c>WebApplicationFactory</c> (§12.4), and two would make it ambiguous.
/// </remarks>
internal static class EntryPoint
{
    private static Task<int> Main(string[] args) =>
        ReplayCommand.RunAsync(
            args,
            Environment.GetEnvironmentVariable,
            Console.Out,
            Console.Error,
            CancellationToken.None);
}
```

`Replay` and `ReplayReport` do not exist yet, so the tool still fails to
build; Task 4 and Task 5 add them. To run this task's tests now, add the two
types as stubs in `tools/bff-replay/Replay.cs`, each member throwing
`NotImplementedException`, and replace them in Task 5:

```csharp
namespace BffReplay;

/// <summary>ADR-051's rebuild; the body arrives with the reset and the read.</summary>
public static class Replay
{
    public const string Queue = "bff-order-events";

    public static readonly TimeSpan BrokerDeadline = TimeSpan.FromSeconds(30);

    public static Task<ReplayReport> RunAsync(
        ReplaySettings settings,
        bool reset,
        TextWriter output,
        TimeSpan brokerDeadline,
        CancellationToken ct) =>
        throw new NotImplementedException();
}

/// <summary>What a replay sent.</summary>
public sealed class ReplayReport
{
    public IReadOnlyList<Guid> SentMessageIds => throw new NotImplementedException();
}
```

- [ ] **Step 6: Run them**

```bash
dotnet build Platform.slnx
dotnet test tests/Web.Bff.Tests --no-build --filter "FullyQualifiedName~ReplayCommandTests"
py -3.12 -m unittest discover -s .github/output-gate
```

Expected: the build succeeds; 5 passed; the gate's suite passes, its
`ThisRepository` class now reading the `tools/` entry this task listed.

- [ ] **Step 7: Commit**

```bash
git add tools/bff-replay Platform.slnx tests/Web.Bff.Tests/Web.Bff.Tests.csproj tests/Web.Bff.Tests/ReplayCommandTests.cs
git commit -m "feat(tools): bff-replay's command line, refusing a bad argument or a missing connection before it opens anything"
```

---

### Task 4: What the rebuild reads, and where it sends

**Files:**
- Create: `tools/bff-replay/ReplayedEvents.cs`
- Create: `tools/bff-replay/OutboxRows.cs`
- Create: `tools/bff-replay/ReplayPayload.cs`
- Create: `tests/Web.Bff.Tests/ReplayTargetTests.cs`

- [ ] **Step 1: Write the failing subject tests**

These hold each thing the tool reads or targets to the code that owns it,
because a replay that matched nothing would print zeroes and look like an
empty window.

`tests/Web.Bff.Tests/ReplayTargetTests.cs`:

```csharp
using BffReplay;
using Common.Application;
using Common.Contracts;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;
using MessagingRegistration = Web.Bff.Messaging.DependencyInjection;

namespace Web.Bff.Tests;

/// <summary>What the rebuild reads and where it sends, each held to the code that owns it.</summary>
public sealed class ReplayTargetTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly MessageTypeMap Contracts = new([typeof(IIntegrationEvent).Assembly]);

    public static TheoryData<string> Publishers => [.. Publisher.All.Select(p => p.Name)];

    [Fact]
    public void The_tool_replays_exactly_the_events_the_queue_binds() =>
        ReplayedEvents.Types.ShouldBe(MessagingRegistrationTests.Consumed, ignoreOrder: true);

    [Fact]
    public void The_tool_sends_to_the_queue_the_bff_consumes() =>
        Replay.Queue.ShouldBe(MessagingRegistration.EventsQueue);

    [Fact]
    public void Every_replayed_event_belongs_to_exactly_one_publisher()
    {
        foreach (Type replayed in ReplayedEvents.Types)
            Publisher.All.Count(p => ReplayedEvents.PublishedBy(p).Contains(replayed)).ShouldBe(1, replayed.Name);
    }

    [Theory]
    [MemberData(nameof(Publishers))]
    public void Each_publisher_s_outbox_is_the_table_its_service_maps(string name)
    {
        Publisher publisher = Publisher.All.Single(p => p.Name == name);
        string configuration = File.ReadAllText(RepositoryFile.Locate(
            $"src/Services/{name}/{name}.Infrastructure/Persistence/OutboxMessageConfiguration.cs"));

        configuration.ShouldContain($"builder.ToTable(\"OutboxMessages\", \"{publisher.Schema}\");");
    }

    [Fact]
    public void A_staged_event_reads_back_as_the_event_its_publisher_wrote()
    {
        OrderPlaced placed = OrderEvents.Placed(Guid.CreateVersion7(), Guid.CreateVersion7(), At);
        OutboxRow row = Row(placed, placed.MessageId);

        IIntegrationEvent read = ReplayPayload.Read(row, Contracts, new OutboxJson([]));

        OrderPlaced back = read.ShouldBeOfType<OrderPlaced>();
        back.MessageId.ShouldBe(placed.MessageId);
        back.OccurredAt.ShouldBe(placed.OccurredAt);
        back.Lines.ShouldBe(placed.Lines);
    }

    [Fact]
    public void A_row_whose_payload_names_another_message_stops_the_replay()
    {
        OrderPlaced placed = OrderEvents.Placed(Guid.CreateVersion7(), Guid.CreateVersion7(), At);
        OutboxRow row = Row(placed, Guid.CreateVersion7());

        InvalidOperationException refused = Should.Throw<InvalidOperationException>(
            () => ReplayPayload.Read(row, Contracts, new OutboxJson([])));

        refused.Message.ShouldContain("sends the row's own message or nothing");
    }

    // Staged through the publishers' own path, so the payload is the bytes a service writes (§9.4).
    private static OutboxRow Row(IIntegrationEvent message, Guid rowMessageId)
    {
        OutboxMessage staged = OutboxMessage.Stage(
            message,
            OutboxLane.Broker,
            message.CorrelationId,
            Contracts,
            new OutboxJson([]));

        return new OutboxRow(
            1,
            rowMessageId,
            staged.CorrelationId,
            staged.MessageType,
            staged.Payload,
            staged.OccurredAt);
    }
}
```

The column names need no test: `OutboxRows` spells them with
`nameof(OutboxMessage.…)`, so a renamed outbox column fails the build.

- [ ] **Step 2: Run them**

```bash
dotnet build Platform.slnx
```

Expected: CS0246 for `ReplayedEvents`, `OutboxRow` and `ReplayPayload`, and
CS0117 for `Replay.Queue` only if Task 3's stub was skipped.

- [ ] **Step 3: The eight, the rows and the payload**

`tools/bff-replay/ReplayedEvents.cs`:

```csharp
using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace BffReplay;

/// <summary>ADR-051's eight events: what <c>bff-order-events</c> binds, and the only rows a replay sends.</summary>
public static class ReplayedEvents
{
    public static readonly IReadOnlyList<Type> Types =
    [
        typeof(OrderPlaced),
        typeof(OrderConfirmed),
        typeof(OrderCancelled),
        typeof(PaymentAuthorised),
        typeof(PaymentRefunded),
        typeof(ShipmentDispatched),
        typeof(ShipmentDelivered),
        typeof(ProductPublished)
    ];

    /// <summary>The share a publisher owns, selected by the contract namespace its name gives (§9.2).</summary>
    public static IReadOnlyList<Type> PublishedBy(Publisher publisher) =>
        [.. Types.Where(type => type.Namespace == publisher.ContractNamespace)];
}
```

`tools/bff-replay/OutboxRows.cs`:

```csharp
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Data.SqlClient;

namespace BffReplay;

/// <summary>One processed outbox row, the six columns a replay needs of it.</summary>
public sealed record OutboxRow(
    long Id,
    Guid MessageId,
    Guid CorrelationId,
    string MessageType,
    string Payload,
    DateTimeOffset OccurredAt);

/// <summary>Step three of the rebuild: a publisher's processed Broker-lane rows of its share of the eight.</summary>
public static class OutboxRows
{
    /// <summary>Named from the row type, so a renamed column fails the build rather than reading nothing.</summary>
    private static readonly string Columns = string.Join(
        ", ",
        nameof(OutboxMessage.Id),
        nameof(OutboxMessage.MessageId),
        nameof(OutboxMessage.CorrelationId),
        nameof(OutboxMessage.MessageType),
        nameof(OutboxMessage.Payload),
        nameof(OutboxMessage.OccurredAt));

    /// <summary>Reads no row, and fails on a missing table, column or grant: the preflight's question.</summary>
    public static string ProbeSql(Publisher publisher) => $"SELECT TOP (0) {Columns} FROM {publisher.Outbox};";

    /// <summary>Processed rows only: an unprocessed one is still the dispatcher's, and reaches the queue so.</summary>
    public static string ReadSql(Publisher publisher) =>
        $"""
        SELECT {Columns}
        FROM {publisher.Outbox}
        WHERE {nameof(OutboxMessage.ProcessedAt)} IS NOT NULL
            AND {nameof(OutboxMessage.Lane)} = @Lane
            AND {nameof(OutboxMessage.MessageType)} IN @Names
        ORDER BY {nameof(OutboxMessage.OccurredAt)}, {nameof(OutboxMessage.Id)};
        """;

    /// <summary>Unbuffered, since a window is whatever the publisher kept, not this process's to hold.</summary>
    public static IAsyncEnumerable<OutboxRow> ReadAsync(
        SqlConnection connection,
        Publisher publisher,
        IReadOnlyList<string> names) =>
        connection.QueryUnbufferedAsync<OutboxRow>(
            ReadSql(publisher),
            new { Lane = nameof(OutboxLane.Broker), Names = names });
}
```

`tools/bff-replay/ReplayPayload.cs`:

```csharp
using System.Text.Json;
using Common.Contracts;
using Common.Infrastructure.Outbox;

namespace BffReplay;

/// <summary>Turns a row back into its publisher's event, through the map and serialiser that wrote it.</summary>
public static class ReplayPayload
{
    public static IIntegrationEvent Read(OutboxRow row, MessageTypeMap types, OutboxJson json)
    {
        Type type = types.Resolve(row.MessageType);
        object? payload = JsonSerializer.Deserialize(row.Payload, type, json.Options);

        // The row's id is the one the BFF's inbox keys on (§9.5), so a payload naming another is a stranger.
        return payload is IIntegrationEvent message && message.MessageId == row.MessageId
            ? message
            : throw new InvalidOperationException(
                $"Outbox row {row.Id} names message {row.MessageId}, and its payload reads as " +
                $"{(payload as IIntegrationEvent)?.MessageId.ToString() ?? "no integration event"}. A replay " +
                "sends the row's own message or nothing, so this row stops the run.");
    }
}
```

The `OutboxJson` the tool builds carries no converter: a converter exists for
a domain event's value object (§9.4), and the eight are contracts of
primitives (§9.1).

- [ ] **Step 4: Run them**

```bash
dotnet build Platform.slnx
dotnet test tests/Web.Bff.Tests --no-build --filter "FullyQualifiedName~ReplayTargetTests"
```

Expected: 9 passed — three facts, four theory rows, two payload facts.

- [ ] **Step 5: Commit**

```bash
git add tools/bff-replay tests/Web.Bff.Tests/ReplayTargetTests.cs
git commit -m "feat(tools): bff-replay reads the eight from each publisher's processed outbox rows through the code that wrote them"
```

---

### Task 5: The reset, the replay and the report, against the consumers

**Files:**
- Create: `tools/bff-replay/ProjectionReset.cs`
- Modify: `tools/bff-replay/Replay.cs` (Task 3's stub, replaced whole)
- Create: `tools/bff-replay/ReplayReport.cs` (Task 3's stub moves here)
- Modify: `tests/Web.Bff.Tests/BffServiceFixture.cs`
- Create: `tests/Web.Bff.Tests/PublisherOutboxes.cs`
- Create: `tests/Web.Bff.Tests/ReplayTests.cs`

- [ ] **Step 1: The fixture reads the broker's address and a queue's depth**

Add to `tests/Web.Bff.Tests/BffServiceFixture.cs`, beside PR-2's broker
members, with `using System.Globalization;` if the file lacks it:

```csharp
    /// <summary>The container's broker, under the fixture's derived account, for a client outside the host.</summary>
    public string BrokerAddress => BrokerConnectionString;

    /// <summary>The messages on one queue, or zero for a queue the broker has never declared.</summary>
    public async Task<int> QueueDepthAsync(string queue) =>
        (await BrokerRowsAsync(["list_queues", "name", "messages"]))
            .Where(columns => columns.Length == 2 && columns[0] == queue)
            .Select(columns => int.Parse(columns[1], CultureInfo.InvariantCulture))
            .SingleOrDefault();
```

`BrokerConnectionString` and `BrokerRowsAsync` are the shared body's
protected members (ADR-056); the fixture exposes the two reads this suite
needs and nothing else of the container.

- [ ] **Step 2: The publishers' outboxes, staged as each service writes them**

`tests/Web.Bff.Tests/PublisherOutboxes.cs`:

```csharp
using BffReplay;
using Common.Application;
using Common.Contracts;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Web.Bff.Tests;

/// <summary>Four publishers' outbox tables in one database of the fixture's server, staged as each writes.</summary>
/// <remarks>
/// The DDL is every service's <c>OutboxMessageConfiguration</c> in SQL (§9.4); the rows go through
/// <see cref="OutboxMessage.Stage"/>, so their payloads are the publishers' own bytes.
/// </remarks>
internal sealed class PublisherOutboxes(string serverConnectionString)
{
    private const string Database = "ReplayPublishers";

    private static readonly MessageTypeMap Contracts = new([typeof(IIntegrationEvent).Assembly]);

    private static readonly OutboxJson Json = new([]);

    public string ConnectionString { get; } =
        new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = Database }.ConnectionString;

    /// <summary>Creates the database and its four tables once, and empties them on every call.</summary>
    public async Task ResetAsync()
    {
        SqlConnectionStringBuilder master = new(serverConnectionString) { InitialCatalog = "master" };
        await using (SqlConnection server = new(master.ConnectionString))
        {
            await server.ExecuteAsync($"IF DB_ID(N'{Database}') IS NULL CREATE DATABASE [{Database}];");
        }

        await using SqlConnection connection = new(ConnectionString);
        foreach (Publisher publisher in Publisher.All)
        {
            await connection.ExecuteAsync(
                $"""
                IF SCHEMA_ID(N'{publisher.Schema}') IS NULL EXEC (N'CREATE SCHEMA [{publisher.Schema}]');
                IF OBJECT_ID(N'{publisher.Schema}.OutboxMessages') IS NULL
                    CREATE TABLE {publisher.Outbox}
                    (
                        Id            bigint IDENTITY  NOT NULL PRIMARY KEY,
                        MessageId     uniqueidentifier NOT NULL,
                        CorrelationId uniqueidentifier NOT NULL,
                        MessageType   nvarchar(300)    NOT NULL,
                        Payload       nvarchar(max)    NOT NULL,
                        Lane          varchar(16)      NOT NULL,
                        OccurredAt    datetimeoffset   NOT NULL,
                        ProcessedAt   datetimeoffset   NULL,
                        Attempts      int              NOT NULL,
                        LastError     nvarchar(2000)   NULL,
                        LockedUntil   datetimeoffset   NULL
                    );
                TRUNCATE TABLE {publisher.Outbox};
                """);
        }
    }

    /// <summary>Stages events in one publisher's outbox, processed at the instant given or not at all.</summary>
    public async Task StageAsync(Publisher publisher, DateTimeOffset? processedAt, params IIntegrationEvent[] events)
    {
        await using SqlConnection connection = new(ConnectionString);
        foreach (IIntegrationEvent message in events)
        {
            OutboxMessage row = OutboxMessage.Stage(message, OutboxLane.Broker, message.CorrelationId, Contracts, Json);

            await connection.ExecuteAsync(
                $"""
                INSERT INTO {publisher.Outbox}
                    (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, ProcessedAt, Attempts)
                VALUES (@MessageId, @CorrelationId, @MessageType, @Payload, @Lane, @OccurredAt, @ProcessedAt, 0);
                """,
                new
                {
                    row.MessageId,
                    row.CorrelationId,
                    row.MessageType,
                    row.Payload,
                    Lane = row.Lane.ToString(),
                    row.OccurredAt,
                    ProcessedAt = processedAt
                });
        }
    }
}
```

The widths are `MessageTypeMap.MaxNameLength`, `OutboxMessage.LaneMaxLength`
and `LastErrorMaxLength` as Ordering's configuration applies them; the tool
reads six of these columns, by `nameof`, so a drift between this DDL and a
service's is caught where it matters — at the read the tool makes in
production — and not here.

- [ ] **Step 3: Write the failing replay tests**

`tests/Web.Bff.Tests/ReplayTests.cs`:

```csharp
using BffReplay;
using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-051's rebuild against the real schema and broker, compared with what the consumers built.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class ReplayTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Processed = At.AddDays(3);

    private readonly PublisherOutboxes _outboxes = new(fixture.ConnectionString);

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        await _outboxes.ResetAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_reset_rebuilds_the_projection_the_consumers_built_from_the_same_events()
    {
        Guid kept = Guid.CreateVersion7();
        Guid cancelled = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        ProductPublished published = OrderEvents.Published(OrderEvents.Lamp, "Walnut desk lamp", At.AddDays(-3));
        OrderPlaced placedKept = OrderEvents.Placed(kept, customer, At);
        OrderConfirmed confirmed = OrderEvents.Confirmed(kept, customer, At.AddSeconds(7));
        PaymentAuthorised authorised = OrderEvents.Authorised(kept, At.AddSeconds(5));
        ShipmentDispatched dispatched = OrderEvents.Dispatched(kept, At.AddDays(1));
        OrderPlaced placedCancelled = OrderEvents.Placed(cancelled, customer, At.AddMinutes(1));
        OrderCancelled cancellation = OrderEvents.Cancelled(
            cancelled,
            customer,
            At.AddMinutes(2),
            CancelReasons.PaymentDeclined,
            CancelOrigins.Workflow);
        PaymentRefunded refunded = OrderEvents.Refunded(cancelled, At.AddMinutes(3));

        // One by one, so each is sent as its own contract rather than as the interface.
        await fixture.DeliverAsync(published);
        await fixture.DeliverAsync(placedKept);
        await fixture.DeliverAsync(confirmed);
        await fixture.DeliverAsync(authorised);
        await fixture.DeliverAsync(dispatched);
        await fixture.DeliverAsync(placedCancelled);
        await fixture.DeliverAsync(cancellation);
        await fixture.DeliverAsync(refunded);
        Guid[] handled =
        [
            published.MessageId, placedKept.MessageId, confirmed.MessageId, authorised.MessageId,
            dispatched.MessageId, placedCancelled.MessageId, cancellation.MessageId, refunded.MessageId
        ];

        ProjectedOrder keptBefore = (await fixture.OrderAsync(kept)).ShouldNotBeNull();
        ProjectedOrder cancelledBefore = (await fixture.OrderAsync(cancelled)).ShouldNotBeNull();
        IReadOnlyList<ProjectedLine> keptLines = await fixture.LinesAsync(kept);
        IReadOnlyList<ProjectedLine> cancelledLines = await fixture.LinesAsync(cancelled);

        // Three rows a replay must not send: two types outside the eight, and one the dispatcher still owns.
        PriceChanged repriced = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = OrderEvents.Lamp,
            OccurredAt = At.AddDays(-2),
            ProductId = OrderEvents.Lamp,
            Amount = 24.99m,
            Currency = OrderEvents.Currency
        };
        PaymentDeclined declined = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = cancelled,
            OccurredAt = At.AddMinutes(1).AddSeconds(30),
            OrderId = cancelled,
            Reason = "card_declined"
        };
        ShipmentDelivered undispatched = OrderEvents.Delivered(kept, At.AddDays(2));

        await _outboxes.StageAsync(Named("Catalog"), Processed, published, repriced);
        await _outboxes.StageAsync(Named("Ordering"), Processed, placedKept, confirmed, placedCancelled, cancellation);
        await _outboxes.StageAsync(Named("Payments"), Processed, authorised, refunded, declined);
        await _outboxes.StageAsync(Named("Shipping"), Processed, dispatched);
        await _outboxes.StageAsync(Named("Shipping"), null, undispatched);

        StringWriter output = new();
        ReplayReport report = await Replay.RunAsync(
            Settings(),
            reset: true,
            output,
            Replay.BrokerDeadline,
            TestContext.Current.CancellationToken);

        report.SentMessageIds.ShouldBe(handled, ignoreOrder: true);
        await BffServiceFixture.WaitUntilAsync(async () => (await fixture.InboxAsync()).Count == handled.Length);

        (await fixture.OrderAsync(kept)).ShouldNotBeNull().Facts().ShouldBe(keptBefore.Facts());
        (await fixture.OrderAsync(cancelled)).ShouldNotBeNull().Facts().ShouldBe(cancelledBefore.Facts());
        (await fixture.LinesAsync(kept)).ShouldBe(keptLines);
        (await fixture.LinesAsync(cancelled)).ShouldBe(cancelledLines);
        (await fixture.QueueDepthAsync($"{Replay.Queue}_skipped"))
            .ShouldBe(0, "a type the queue does not bind was sent");

        report.OldestFrom(Named("Catalog")).ShouldBe(published.OccurredAt);
        output.ToString().ShouldContain("Shipping: 1 event(s) sent");
    }

    [Fact]
    public async Task A_replay_without_reset_fills_what_the_bff_never_handled_and_changes_nothing_it_had()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        OrderPlaced placed = OrderEvents.Placed(order, customer, At);
        PaymentAuthorised authorised = OrderEvents.Authorised(order, At.AddSeconds(5));
        ShipmentDispatched missed = OrderEvents.Dispatched(order, At.AddDays(1));

        await fixture.DeliverAsync(placed);
        await fixture.DeliverAsync(authorised);
        ProjectedOrder before = (await fixture.OrderAsync(order)).ShouldNotBeNull();

        await _outboxes.StageAsync(Named("Ordering"), Processed, placed);
        await _outboxes.StageAsync(Named("Payments"), Processed, authorised);
        await _outboxes.StageAsync(Named("Shipping"), Processed, missed);

        ReplayReport report = await Replay.RunAsync(
            Settings(),
            reset: false,
            TextWriter.Null,
            Replay.BrokerDeadline,
            TestContext.Current.CancellationToken);

        report.SentMessageIds.Count.ShouldBe(3, "a repair sends the window and leaves the inbox to drop what it has");
        await BffServiceFixture.WaitUntilAsync(async () => (await fixture.InboxAsync()).Count == 3);
        await BffServiceFixture.WaitUntilAsync(async () => await fixture.QueueDepthAsync(Replay.Queue) == 0);

        ProjectedOrder after = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        after.DispatchedAt.ShouldBe(missed.OccurredAt);
        (after with { DispatchedAt = null, TrackingNumber = null, AsOf = before.AsOf }).ShouldBe(before);
        (await fixture.InboxAsync(placed.MessageId)).Count.ShouldBe(1, "the inbox drops a copy it has handled");
    }

    [Fact]
    public async Task A_reset_keeps_the_product_names_no_outbox_still_holds()
    {
        Guid order = Guid.CreateVersion7();
        OrderPlaced placed = OrderEvents.Placed(order, Guid.CreateVersion7(), At);

        await fixture.DeliverAsync(OrderEvents.Published(OrderEvents.Lamp, "Walnut desk lamp", At.AddDays(-400)));
        await fixture.DeliverAsync(placed);
        await _outboxes.StageAsync(Named("Ordering"), Processed, placed);

        await Replay.RunAsync(
            Settings(),
            reset: true,
            TextWriter.Null,
            Replay.BrokerDeadline,
            TestContext.Current.CancellationToken);

        // The reset took both inbox rows, so the one back is the replayed placement's.
        await BffServiceFixture.WaitUntilAsync(async () => (await fixture.InboxAsync()).Count == 1);
        (await fixture.InboxAsync()).ShouldHaveSingleItem().MessageId.ShouldBe(placed.MessageId);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Name FROM bff.Products WHERE ProductId = {0}",
            OrderEvents.Lamp)).ShouldBe("Walnut desk lamp");
        (await fixture.OrderAsync(order)).ShouldNotBeNull().PlacedAt.ShouldBe(At);
    }

    [Fact]
    public async Task A_broker_it_cannot_reach_stops_the_run_before_anything_is_deleted()
    {
        OrderPlaced placed = OrderEvents.Placed(Guid.CreateVersion7(), Guid.CreateVersion7(), At);
        await fixture.DeliverAsync(placed);

        InvalidOperationException refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            Replay.RunAsync(
                Settings(broker: BffFactory.UnreachableBroker),
                reset: true,
                TextWriter.Null,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        refused.Message.ShouldContain("did not answer");
        (await fixture.OrderAsync(placed.OrderId)).ShouldNotBeNull();
        (await fixture.InboxAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_publisher_it_cannot_read_stops_the_run_before_anything_is_deleted()
    {
        OrderPlaced placed = OrderEvents.Placed(Guid.CreateVersion7(), Guid.CreateVersion7(), At);
        await fixture.DeliverAsync(placed);
        string absent = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "NoSuchOutbox"
        }.ConnectionString;

        SqlException refused = await Should.ThrowAsync<SqlException>(() =>
            Replay.RunAsync(
                Settings(publishers: absent),
                reset: true,
                TextWriter.Null,
                Replay.BrokerDeadline,
                TestContext.Current.CancellationToken));

        // 4060: the login cannot open the database it names.
        refused.Number.ShouldBe(4060);
        (await fixture.OrderAsync(placed.OrderId)).ShouldNotBeNull();
    }

    private static Publisher Named(string name) => Publisher.All.Single(p => p.Name == name);

    private ReplaySettings Settings(string? broker = null, string? publishers = null) =>
        new(
            fixture.ConnectionString,
            broker ?? fixture.BrokerAddress,
            [.. Publisher.All.Select(p => new PublisherConnection(p, publishers ?? _outboxes.ConnectionString))]);
}
```

- [ ] **Step 4: Run them**

```bash
dotnet build Platform.slnx
dotnet test tests/Web.Bff.Tests --no-build --filter "FullyQualifiedName~ReplayTests"
```

Expected: the build fails with CS1061 for `OldestFrom` on Task 3's stub, or,
with that line removed to see the run, 5 failed on `NotImplementedException`.

- [ ] **Step 5: The reset**

`tools/bff-replay/ProjectionReset.cs`:

```csharp
using Common.Infrastructure.Inbox;
using Dapper;
using Microsoft.Data.SqlClient;
using Web.Bff.Persistence;

namespace BffReplay;

/// <summary>Step two of the rebuild: the order rows and the queue's inbox rows, deleted in one transaction.</summary>
/// <remarks>
/// <c>bff.Products</c> survives: a name is published once and usually predates every outbox window, and its upsert
/// guards on <c>OccurredAt</c>, so a replayed <c>ProductPublished</c> over a kept row is harmless (ADR-051, §6.6).
/// </remarks>
public static class ProjectionReset
{
    /// <summary>Lines first, so the delete is complete without leaning on the foreign key's cascade.</summary>
    public static readonly string Sql =
        $"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DELETE FROM [{BffSchema.Name}].OrderLines;
        DELETE FROM [{BffSchema.Name}].Orders;
        DELETE FROM {new InboxTable(BffSchema.Name).QualifiedName} WHERE Endpoint = @Endpoint;

        COMMIT;
        """;

    public static async Task RunAsync(string connectionString, CancellationToken ct)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.ExecuteAsync(
            new CommandDefinition(Sql, new { Endpoint = Replay.Queue }, cancellationToken: ct));
    }
}
```

The inbox's `Endpoint` is the receive address's path without its leading
slash (`InboxFilter<T>`), which on the default virtual host is the queue's
name; the third test is what proves the two agree, since it reads back an
inbox holding only the replayed row.

- [ ] **Step 6: The report**

Delete Task 3's `ReplayReport` stub from `Replay.cs`.
`tools/bff-replay/ReplayReport.cs`:

```csharp
namespace BffReplay;

/// <summary>What a replay sent: the ids, a count per type, and how far back each publisher's window reached.</summary>
public sealed class ReplayReport
{
    private readonly List<Guid> _sent = [];

    private readonly SortedDictionary<string, int> _byType = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (int Count, DateTimeOffset Oldest)> _byPublisher = [];

    public IReadOnlyList<Guid> SentMessageIds => _sent;

    public IReadOnlyDictionary<string, int> SentByType => _byType;

    /// <summary>The oldest <c>OccurredAt</c> a publisher held: the window this replay reached there.</summary>
    public DateTimeOffset? OldestFrom(Publisher publisher) =>
        _byPublisher.TryGetValue(publisher.Name, out (int Count, DateTimeOffset Oldest) reached)
            ? reached.Oldest
            : null;

    /// <summary>One line per publisher, printed as each finishes, so a run that fails says how far it got.</summary>
    public string Describe(Publisher publisher) =>
        _byPublisher.TryGetValue(publisher.Name, out (int Count, DateTimeOffset Oldest) reached)
            ? $"{publisher.Name}: {reached.Count} event(s) sent, the oldest from {reached.Oldest:O}."
            : $"{publisher.Name}: none of ADR-051's eight in its outbox window.";

    internal void Sent(Publisher publisher, string messageType, OutboxRow row)
    {
        _sent.Add(row.MessageId);
        _byType[messageType] = _byType.GetValueOrDefault(messageType) + 1;

        (int Count, DateTimeOffset Oldest) reached = _byPublisher.GetValueOrDefault(
            publisher.Name,
            (0, DateTimeOffset.MaxValue));
        _byPublisher[publisher.Name] =
            (reached.Count + 1, row.OccurredAt < reached.Oldest ? row.OccurredAt : reached.Oldest);
    }
}
```

- [ ] **Step 7: The replay**

`tools/bff-replay/Replay.cs`, replacing Task 3's stub whole:

```csharp
using Common.Contracts;
using Common.Infrastructure.Outbox;
using Dapper;
using MassTransit;
using Microsoft.Data.SqlClient;

namespace BffReplay;

/// <summary>ADR-051's rebuild, each step refusing to start until the one before it has completed.</summary>
public static class Replay
{
    /// <summary>The BFF's queue, held equal to the host's own constant by its suite.</summary>
    public const string Queue = "bff-order-events";

    /// <summary>How long the broker has to answer before anything is deleted.</summary>
    public static readonly TimeSpan BrokerDeadline = TimeSpan.FromSeconds(30);

    public static async Task<ReplayReport> RunAsync(
        ReplaySettings settings,
        bool reset,
        TextWriter output,
        TimeSpan brokerDeadline,
        CancellationToken ct)
    {
        MessageTypeMap types = new([typeof(IIntegrationEvent).Assembly]);
        OutboxJson json = new([]);
        List<SqlConnection> sources = [];
        IBusControl bus = Bus.Factory.CreateUsingRabbitMq(cfg => cfg.Host(new Uri(settings.Broker)));
        bool started = false;

        try
        {
            // Every source answers before the reset, so a reset never precedes a read that cannot run.
            foreach (PublisherConnection source in settings.Publishers)
            {
                SqlConnection connection = new(source.ConnectionString);
                sources.Add(connection);
                await connection.OpenAsync(ct);
                await connection.ExecuteAsync(
                    new CommandDefinition(OutboxRows.ProbeSql(source.Publisher), cancellationToken: ct));
            }

            started = await StartAsync(bus, settings.Broker, brokerDeadline, ct);

            ISendEndpoint endpoint = await bus.GetSendEndpoint(new Uri($"queue:{Queue}"));

            if (reset)
            {
                await ProjectionReset.RunAsync(settings.Bff, ct);
                await output.WriteLineAsync($"Reset: the order rows and {Queue}'s inbox rows are deleted.");
            }

            ReplayReport report = new();

            for (int i = 0; i < settings.Publishers.Count; i++)
            {
                Publisher publisher = settings.Publishers[i].Publisher;
                string[] names = [.. ReplayedEvents.PublishedBy(publisher).Select(types.NameOf)];

                await foreach (OutboxRow row in OutboxRows.ReadAsync(sources[i], publisher, names).WithCancellation(ct))
                {
                    IIntegrationEvent message = ReplayPayload.Read(row, types, json);

                    // To the queue alone; the grant would refuse a contract's exchange in any case (ADR-036).
                    await endpoint.Send(
                        message,
                        message.GetType(),
                        c =>
                        {
                            c.MessageId = row.MessageId;
                            c.CorrelationId = row.CorrelationId;
                        },
                        ct);

                    report.Sent(publisher, row.MessageType, row);
                }

                await output.WriteLineAsync(report.Describe(publisher));
            }

            foreach ((string type, int count) in report.SentByType)
                await output.WriteLineAsync($"  {type}: {count}");

            return report;
        }
        finally
        {
            if (started)
                await bus.StopAsync(CancellationToken.None);

            foreach (SqlConnection connection in sources)
                await connection.DisposeAsync();
        }
    }

    // Bounded, so an unreachable broker refuses the run here rather than retrying behind a deleted projection.
    private static async Task<bool> StartAsync(
        IBusControl bus,
        string broker,
        TimeSpan deadline,
        CancellationToken ct)
    {
        string host = new Uri(broker).Host;

        try
        {
            using CancellationTokenSource answered = CancellationTokenSource.CreateLinkedTokenSource(ct);
            answered.CancelAfter(deadline);
            await bus.StartAsync(answered.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"The broker at {host} did not answer within {deadline}, so nothing was deleted or sent.");
        }

        BusHealthStatus health = await bus.WaitForHealthStatus(BusHealthStatus.Healthy, deadline);
        if (health != BusHealthStatus.Healthy)
        {
            await bus.StopAsync(CancellationToken.None);
            throw new InvalidOperationException(
                $"The broker at {host} did not answer within {deadline} (the bus reads {health}), so nothing " +
                "was deleted or sent.");
        }

        return true;
    }
}
```

The message names the host and never the address, which carries the
account's credential. Two seams a build or a run decides rather than this
plan:

- If MassTransit marks `Bus.Factory` obsolete at the pinned version, CS0618
  fails the build under ADR-019, and the bus is built through a
  `ServiceCollection` instead — `AddMassTransit(x => { x.DisableUsageTelemetry();
  x.UsingRabbitMq((context, cfg) => cfg.Host(new Uri(settings.Broker))); })`
  with `AddLogging()` beside it — resolving `IBusControl` and starting it
  the same way. The steps around it do not change.
- If `WaitForHealthStatus` reports a bus with no receive endpoint healthy
  before it has connected, the fourth test fails on the deleted row, and the
  probe becomes the first `Send` instead: the reset moves below one send of
  nothing to an address the account owns. The test is what decides, and it
  stays as written.

- [ ] **Step 8: Run them**

```bash
dotnet build Platform.slnx
dotnet test tests/Web.Bff.Tests --no-build --filter "FullyQualifiedName~ReplayTests|FullyQualifiedName~ReplayTargetTests|FullyQualifiedName~ReplayCommandTests"
```

Expected: 19 passed — 5 replay tests over the containers, 9 target tests, 5
command tests. A binding or permission failure on the first send is the
grant, measured: `bff-svc` sending to its own queue under the BFF's own
account is exactly what PR-2's fixture already does, so a refusal here means
the account changed, and the fix is the account, never a widened harness.

- [ ] **Step 9: Commit**

```bash
git add tools/bff-replay tests/Web.Bff.Tests/BffServiceFixture.cs tests/Web.Bff.Tests/PublisherOutboxes.cs tests/Web.Bff.Tests/ReplayTests.cs
git commit -m "feat(tools): bff-replay resets, replays to bff-order-events alone and reports the window each publisher reached"
```

The body states decisions 3 and 4 — `bff.Products` kept, and the preflight
— and why each (spec section 8).

---

### Task 6: The procedure, beside its tool

**Files:**
- Create: `tools/bff-replay/README.md`

- [ ] **Step 1: Write it**

`tools/bff-replay/README.md`:

````markdown
# bff-replay

ADR-051's rebuild for the BFF's order projection. It sends the eight events
the projection reads to the BFF's queue, from the only place a delivered
event still exists: its publisher's outbox.

## What it can restore, and what it cannot

**A replay reaches back one outbox window and no further.** RabbitMQ keeps
nothing a consumer has acknowledged, so the last copy of a delivered event is
its publisher's processed outbox row, kept for `RetentionPolicy.OutboxWindow`
([§9.4](../../docs/backend-architecture/09-messaging.md)). An order whose
every event is older than the shortest of the four publishers' windows is not
restored by any run of this tool: after `--reset` it is simply absent from
the buyer's history. Recovering that is the BFF database's backup, not this.

So, in order of preference:

1. **Repair** — run with no argument. Every row in the window is sent; the
   BFF's inbox drops each one it has already handled, and the rest fill what
   the projection never received. Nothing is deleted. This is the answer to
   a gap: an order stuck as unattributed, a missing despatch.
2. **Rebuild** — run with `--reset`. The projection's order rows, their lines
   and the queue's inbox rows are deleted in one transaction, then the window
   is replayed. This is the answer to a projection that is *wrong*, not one
   that is incomplete, and it costs every order older than the window.
3. **Restore** — when what is wrong is older than the window, restore the
   BFF's database instead.

**`--reset` keeps `bff.Products`.** A product's name is published once,
usually long before any outbox window, so deleting the names would blank
every product Catalog's window no longer holds. When the names themselves are
wrong, delete from `bff.Products` by hand before the run, knowing that only
products Catalog has published within its window come back.

## What it needs

Six connections, read from its environment, and it refuses to start with any
of them missing — every missing key named in one message:

| Key | What it is |
|---|---|
| `ConnectionStrings__Bff` | The BFF's runtime login ([§7.1](../../docs/backend-architecture/07-persistence.md)), which may delete the projection's rows |
| `ConnectionStrings__RabbitMq` | The BFF's own broker account, `bff-svc` — in a cluster, the value of the Secret the BFF's chart names for its broker |
| `ConnectionStrings__CatalogOutbox` | A login that can read `catalog.OutboxMessages` |
| `ConnectionStrings__OrderingOutbox` | A login that can read `ordering.OutboxMessages` |
| `ConnectionStrings__PaymentsOutbox` | A login that can read `payments.OutboxMessages` |
| `ConnectionStrings__ShippingOutbox` | A login that can read `shipping.OutboxMessages` |

**The broker account is the BFF's on purpose.** `bff-svc`'s grant writes the
BFF's own `bff-` exchanges and no `Common.Contracts` exchange
([ADR-036](../../docs/backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md)),
so the one mistake that would hurt — publishing a replayed event to its
contract's exchange, which redelivers it to every other consumer — is refused
by the broker, whatever the tool does. Do not run it under a wider account.

**The publishers' logins should read and nothing else.** The tool only ever
selects from the four outbox tables, but a service's own runtime login can
write them; mint a login with `SELECT` on the one table for the run, and drop
it afterwards.

Against the local Compose stack, the values are the ones
`deploy/compose/services/web-bff.yml` and each service's own unit give their
hosts, with the container names replaced by `localhost` and the ports
Compose publishes.

## Running it

From the repository root, with the six keys set:

```bash
dotnet run --project tools/bff-replay            # repair
dotnet run --project tools/bff-replay -- --reset # rebuild
```

The BFF must be running: the tool only sends, and the BFF's own consumers
apply what it sends, through the same handlers and the same inbox as live
traffic. Live events arriving during a rebuild are harmless — the projection
ranks facts and never overwrites one
([§10.7](../../docs/backend-architecture/10-api-gateway.md)) — so the run
needs no maintenance window.

Before it deletes anything it opens all four outboxes and waits for the
broker to answer, so an unreachable source stops a `--reset` with the
projection untouched.

## Reading what it prints

One line per publisher as it finishes — how many events it sent, and the
`OccurredAt` of the oldest. **That oldest instant is the window the rebuild
actually reached**: an order placed before the oldest of the four lines is
outside it. Then a count per event type, and the total.

| Exit | Meaning |
|---|---|
| 0 | The window was sent |
| 2 | Refused before anything was opened: a bad argument or a missing key |
| any other | The run failed part-way, and the lines above say which publishers finished. A repair can be run again as it is; a failed `--reset` has left the projection partial, so run `--reset` again |

## What it does not do

- It never writes to a publisher's database and never publishes to an
  exchange.
- It sends no unprocessed row: those are still the publisher's dispatcher's,
  and reach the queue that way.
- It sends nothing outside ADR-051's eight, whatever else the outboxes hold.
- It is not built into an image. It runs where an operator can reach the six
  connections, and its suite is the BFF's, because the proof that it works is
  the BFF's own consumers rebuilding the projection they built the first time.
````

- [ ] **Step 2: Scan and gate it**

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: `0 finding(s)`. The README names keys and never a value.

- [ ] **Step 3: Commit**

```bash
git add tools/bff-replay/README.md
git commit -m "docs(tools): bff-replay's README, the rebuild's procedure, window and grant"
```

The body states decision 1 (spec section 8): `docs/runbooks/` pairs each runbook with an
alert both ways and this PR adds none, so the procedure sits beside its tool
as the scaffold's does, and PR-5's runbook points at it.

---

### Task 7: The chapters, the ADR and the trees

**Files:**
- Modify: `docs/backend-architecture/10-api-gateway.md`
- Modify: `docs/backend-architecture/adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md`
- Modify: `docs/backend-architecture/04-solution-structure.md` (the tree)
- Modify: `docs/repo-map.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: §10.7 names the window**

In §10.7's *An order the projection cannot yet attribute*, the paragraph
ending `…and whose \`ShipmentDelivered\` has not is exactly such a row.`
gains, after that sentence:

```markdown
and whose `ShipmentDelivered` has not is exactly such a row. **That window is
the publishers' outboxes**: RabbitMQ keeps nothing a consumer has
acknowledged, so the last copy of a delivered event is its publisher's
processed outbox row, kept for `RetentionPolicy.OutboxWindow` (§9.4), and
`tools/bff-replay` replays those rows to this projection's queue alone.
```

The sentence before it is not rewritten: what it says about the order stays
true, and the added sentence says where the window is.

- [ ] **Step 2: ADR-051's callout**

Append to ADR-051, after its last paragraph and before the closing `---`,
in ADR-045's callout form:

```markdown
> **The rebuild's window is the publishers' outboxes, and nothing above has
> been edited.** RabbitMQ's classic queues keep nothing a consumer has
> acknowledged, so the window the consequences call the broker's is held by
> the publishers: each keeps its processed outbox rows for
> `RetentionPolicy.OutboxWindow` ([§9.4](../09-messaging.md)), and
> `tools/bff-replay` sends those rows to the BFF's queue alone, under the ids
> and times they were published with.
>
> **What the BFF can select from is still nothing.** The tool is an
> operator's, run against the publishers' tables out of band, and no path in
> the host reads them. An order every one of whose events is older than the
> shortest publisher's window is not rebuilt; recovering it is the database's
> backup.
```

- [ ] **Step 3: §4.1's tree**

In the tree, the `tools/` entry and the `artifacts/` entry after it become:

```text
├── tools/
│   ├── bff-replay/                     ADR-051's rebuild: a console that
│   │                                   replays the publishers' outbox rows to
│   │                                   the BFF's queue. Built with the
│   │                                   solution; its suite is Web.Bff.Tests
│   └── new-service/                    The scaffold of §4.5 and its tests.
│                                       Stdlib Python, no restore — it renders
│                                       a service from Catalog at run time
│
├── artifacts/                          Every build's output: bin, obj and
│                                       publish take a subdirectory per project,
│                                       package groups by configuration.
│                                       Generated and git-ignored, and drawn
│                                       here because it is the reason src/,
│                                       tests/ and tools/ above hold source alone
```

- [ ] **Step 4: The repo map and `CLAUDE.md`**

`docs/repo-map.md`, in the tree after the `tools/new-service/` line:

```text
tools/bff-replay/            ADR-051's rebuild — a console replaying the
                             publishers' processed outbox rows to the BFF's
                             queue alone. Its README is the procedure, since
                             a runbook here is one per alert; its suite is
                             the BFF's, which proves it against the consumers
```

`CLAUDE.md`, in *The tree*, after the `tools/new-service/` line:

```text
tools/bff-replay/            ADR-051's rebuild: the BFF's projection replayed from the publishers' outboxes
```

- [ ] **Step 5: The checks this class owes**

```bash
/validate-blueprint
/check-links
```

`/validate-blueprint` because §4.1 and §10.7 are in its scope (spec,
section 12); `/check-links` because the callout and §4.1's paragraph carry
links under `docs/backend-architecture/`. Fix what either finds in this task.

- [ ] **Step 6: Commit**

```bash
git add docs/backend-architecture/10-api-gateway.md docs/backend-architecture/adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md docs/backend-architecture/04-solution-structure.md docs/repo-map.md CLAUDE.md
git commit -m "docs: §10.7 and ADR-051 name the publishers' outboxes as the rebuild's window; the trees gain tools/bff-replay"
```

---

### Task 8: Everything run, and the PR

**Files:** none.

- [ ] **Step 1: The solution and the gates**

```bash
dotnet build Platform.slnx
py -3.12 .github/output-gate/output_gate.py
dotnet format Platform.slnx --verify-no-changes --no-restore
dotnet test Platform.slnx
py -3.12 -m unittest discover -s .github/output-gate
(cd tools/new-service && py -3.12 -m unittest)
py -3.12 .github/secret-scan/secret_scan.py
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: the build and the output gate pass, the gate's success line naming
`src/, tests/ and tools/`; the format check is clean; the whole suite passes,
the container tests included and none skipped; the scaffold's suite passes,
since its solution splice never reaches a `/tools/` folder; the secret scan
reports nothing; the comment gate passes over every block the branch adds.

- [ ] **Step 2: The pull request**

`/pr`, with the body's `| Class |` row `A+D+E` and its `| Touch set |` row
this plan's Global Constraints line, paths only. Validate both before
opening:

```bash
git diff --name-only origin/main...HEAD
```

and run `main`'s `locality_gate.py --map classes.yml` over the drafted body
and that list, in the input shape `.github/workflows/locality-gate.yml`
gives it. The body names the four decisions and carries a bare `Refs #425`;
it closes the issue this PR was filed under, and not #425, which the last
pull request of the sequence closes.

---

## Interfaces for PR-5

- **The procedure** is `tools/bff-replay/README.md`. PR-5's runbook,
  `docs/runbooks/unattributed-order.md`, for the rule over
  `bff.orders.unattributed`, points its repair step at that file's
  *Repair* paragraph rather than restating the tool, and its "when the gap is
  older than the window" step at the README's *What it can restore*.
- **The command** is `dotnet run --project tools/bff-replay` (repair) and
  `-- --reset` (rebuild); exit 0, 2 for a refusal before anything was opened,
  anything else for a run that failed part-way.
- **The keys** are `ConnectionStrings__Bff`, `ConnectionStrings__RabbitMq`
  (`bff-svc`, the BFF chart's broker Secret) and
  `ConnectionStrings__{Catalog,Ordering,Payments,Shipping}Outbox`.
- **`--reset` keeps `bff.Products`** and deletes `bff.Orders`,
  `bff.OrderLines` and `bff.InboxMessages` rows for `bff-order-events`.
- **The output gate walks `tools/`**, so any later project there is held to
  §4.1 like one under `src/`.

## Self-review

- **Spec, section 1 (*The rebuild, and what it reads*):** the publishers'
  outboxes, not the broker — Task 4's read, Task 7's §10.7 sentence and
  ADR-051 callout; the eight types' processed rows, sent to the queue alone
  with the original `MessageId` and `OccurredAt` — Task 5's `Send` and the
  payload, which carries `OccurredAt` itself; one outbox window, the
  shortest of the four, and the database backup beyond it — the README's
  first section and the callout; neither sentence rewritten — Task 7 Steps 1
  and 2 add and edit nothing above them.
- **Spec, section 3:** the tool under `tools/`, its suite (the BFF's) and its
  procedure — Tasks 3 to 6; the procedure is a README, decision 1.
- **Spec, section 4:** PR-4's row — the tool, the suite, the procedure,
  §10.7's sentence and ADR-051's callout; strictly after PR-2, whose queue
  name, account and handlers Task 1 verifies and Task 4's tests hold the tool
  to.
- **Spec, section 8, step by step:** 1, six connections from the
  environment, refused with any missing — `ReplaySettings.FromEnvironment`
  and Task 3's tests, every missing key named; 2, every connection opened
  and each outbox and the broker probed before anything is deleted —
  decision 4, and the fourth and fifth tests; 3, `--reset` in one
  transaction, the projection's order rows and the queue's inbox rows, and
  without it a repair the inbox makes partial — `ProjectionReset` and
  Task 5's second and third tests; `bff.Products` kept, decision 3; 4, each
  publisher's processed `Broker`-lane rows of the eight, oldest first —
  `OutboxRows.ReadSql`, and the first test's unprocessed row and its two
  types outside the eight never sent; 5, to `queue:bff-order-events` alone,
  never the exchange, with `MessageId`, `CorrelationId` and `OccurredAt` —
  the `Send`, the account's grant, and the skipped queue left empty; 6, a
  count per type and the oldest `OccurredAt` per publisher —
  `ReplayReport.Describe` and the type lines, asserted in the first test.
  The references are `Common.Infrastructure` (with `Common.Contracts`) and
  `Web.Bff.Persistence` for the schema's name, and no service.
- **Spec, section 8, the window paragraph:** the README opens with it, says
  repair before rebuild, and restore beyond the window.
- **Spec, section 11, the rebuild's line:** rows staged in four publisher
  schemas through `OutboxMessage.Stage`; a `--reset` run producing the
  projection the consumers produced from the same events, compared by
  `Facts()` and the lines; a type outside the eight never sent, asserted from
  the report and from the broker's skipped queue.
- **Spec, section 12:** §10.7 gains the window's sentence and ADR-051 the
  callout, both here; §4.1's tree, the repo map and `CLAUDE.md`'s tree are
  added because each is one line per entry and the tool is an entry.
- **Class A+D+E:** every path is in the touch-set line; `tests/**` is A,
  `tools/**`, `.github/**`, the chapters, the repo map and `CLAUDE.md` are D,
  `Platform.slnx` and the `.csproj` files are E. No `src/**` path is edited.
- **Gates that read the new surface:** the output gate (Task 2, by a root,
  with `ThisRepository` as the test whose subject is what it walks); the
  format check, by the solution entry; the licence gate, which globs every
  `.csproj` from the root and needs no change; the comment gate, which
  reaches C#, `.csproj`, Python and YAML; the secret scan, which reads
  `tools/`. The pipeline gate's filters read `src/` deployables, and the tool
  is none: it has no image and no chart.
- **No printed credential**: the plan and the README name keys and files,
  and the secret scan runs over both in Tasks 6 and 8.
