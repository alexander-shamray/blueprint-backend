# Retry-safe writes PR-A — the command fingerprint — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store the result of an idempotent command with a SHA-256
fingerprint of the command that produced it, and refuse a retry that carries
a different command under the same `CommandId` with a new 409
`command.id_reused` instead of answering it with the first command's result.

**Architecture:** `CommandFingerprint.Of` hashes the command as the pipeline
holds it, and `IdempotencyBehavior` calls it once, before the claim. `Capture`
writes `sha256:{fingerprint}:{json}` into the payload the behaviour already
owns and `Replay` compares before it reads, so `IIdempotencyStore`, both Redis
scripts and the marker row are untouched and no service migrates. A mismatch
throws `CommandIdReusedException` from `Common.Application`, which a fourth
409 handler in `Common.Web` translates; ADR-057 records the rule and amends
§8.5, and §10.5's table gains the row.

**Tech Stack:** `System.Text.Json` and `System.Security.Cryptography` from the
shared framework — no package is added — xUnit v3, Shouldly, and for the one
wiring test Ordering's existing `ServiceFixture` over Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-01-retry-safe-writes-design.md`:
*What exists, and is kept*, gap 1 of *The two gaps*, the whole of
*Decision 1*, PR-A's row in *Delivery*, and the first four bullets of
*Testing* (`CommandFingerprint`, `IdempotencyBehavior`,
`CommandIdReusedExceptionHandler`, and the end-to-end Ordering case).
Decisions 2 and 3 are PR-C's and PR-B's plans and nothing here touches them.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class C.** The PR body's row is this line, and nothing else goes in the
  cell — paths only, comma-separated, no prose and no trailing stop, because
  `.github/locality-gate/locality_gate.py` refuses a row whose last token
  does not end in its backtick:

  ```
  | Touch set | `src/BuildingBlocks/Common.Application/**`, `src/BuildingBlocks/Common.Web/**`, `tests/Common.Application.Tests/**`, `tests/Common.Web.Tests/**`, `tests/Ordering.Api.Tests/PlaceOrderTests.cs`, `docs/backend-architecture/adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md`, `docs/backend-architecture/appendix-a-adrs.md`, `docs/backend-architecture/08-caching-redis.md`, `docs/backend-architecture/10-api-gateway.md` |
  ```

  Why each: `Common.Application` holds the fingerprint, the exception and the
  behaviour; `Common.Web` holds the handler and its registration; the two
  `Common.*.Tests` directories are their suites; `PlaceOrderTests.cs` is the
  one service test that proves the wiring; the ADR file is Class C's one new
  ADR and `appendix-a-adrs.md` is its index row; `08-caching-redis.md` is the
  section the ADR amends; `10-api-gateway.md` owns the status mapping, so the
  row for the new code can go nowhere else. The row was run through the
  locality gate against the prototype's file list and passed.
- **Every count, line number and expected test total in this plan is a
  measurement on `main` at `b07cd0b2`.** Read them as what the prototype saw,
  not as a claim about the tree you meet; a line number that has moved is
  found by the quoted text beside it.
- **The ADR number is the next free one when Task 1 runs.** This plan says
  ADR-057 throughout. If `docs/backend-architecture/adr/` already holds an
  ADR-057, take the next free number by `/new-adr`'s rule and substitute it
  everywhere this plan prints it: the file name, the heading, the Appendix A
  row, the chapter links, the touch-set row and the comments in the code.
- **`.cs` files are CRLF** (`.gitattributes`, `*.cs text eol=crlf`), and an LF
  one fails the build with one IDE0055 per line. The Write tool emits LF, so
  run `unix2dos <path>` on every `.cs` file this plan creates, straight after
  writing it, and confirm that `file <path>` reports CRLF line terminators —
  after an edit to an existing `.cs` file too. Never run `sed -i` on a `.cs`
  file under Git Bash: measured in the prototype, it rewrites the file with LF
  endings.
- **No package, no pin, no project file.** `System.Text.Json` and
  `System.Security.Cryptography` are in the shared framework. No `.csproj` is
  edited: `CommandFingerprint` stays `internal`, as the spec pins, and
  `Common.Application` gains no `InternalsVisibleTo` — no building block
  carries one, and §13.4's and §13.6's samples argue against one. The suites
  read the fingerprint off the payload the public behaviour stores, which is
  how `Common.Application.Tests` already reaches the internal `Dispatcher`.
- **`CommandFingerprint.Of` calls the generic `SerializeToUtf8Bytes<TValue>`
  overload**, as the spec does, not the `(object, Type, options)` one. Under
  ADR-019 the `Type`-taking overload is `error CA2263` in this build; with
  `TValue` bound to `TCommand` the generic overload serialises the same
  declared type to the same bytes.
- **`py -3.12`, never `python`.**
- Code obeys `docs/style-guide.md` as written here, because it will be pasted:
  file-scoped namespaces with a blank line after, explicit local types except
  the four `var` cases, 120 columns, one space before `=`, `=>` and `{` and
  never a column of them, braces on any body that wraps, lists on one line or
  one element per line. A comment says why and cites its owner — a section, an
  ADR or a symbol; a `<summary>` is one sentence, a `<remarks>` cites and is
  four lines or fewer, a block is five lines or fewer; no history, no pull
  request, review or test named, no emphasis. The comment gate also refuses a
  PR whose added C# comment lines outnumber its added code lines.
- **The branch is cut by `/branch`, in a worktree under `.claude/worktrees/`.**
  Nothing is committed on `main`. The checkout is shared with other agents, so
  run `git branch --show-current` before every commit, and stage by the exact
  paths each task names — never `git add -A` and never `/commit`'s unscoped
  form.
- **A commit message is given as a heredoc.** If the harness refuses a
  heredoc in a `git` command, write the same text to a scratchpad file and
  pass it with `git commit -F <file>`; the message does not change. Each ends
  with the attribution lines the session's own reminder gives.
- **`docs/superpowers/` is not in this PR.** The spec and this plan are Class
  D files outside the touch set. They must be committed on `main` through
  their own `docs:` PR before Task 1: untracked, they make `main` dirty, and
  `/branch` then branches in place and carries them along.
- **Not touched, whatever a review suggests:**
  `src/BuildingBlocks/Common.Infrastructure/Redis/RedisIdempotencyStore.cs`
  and its tests, `IIdempotencyStore.cs`, `IdempotencyEntry`, the marker store
  and every migration, any service's `src/` tree, any other service's tests,
  `tools/new-service`, `CLAUDE.md`, Appendix D, `docs/pr-decision-log.md`,
  `docs/repo-map.md`, and any paragraph of §8.5 or §10.5 this plan does not
  quote. §8.5's opening rule is ADR-058's to amend, in PR-C.
- **`/validate-blueprint` runs after a Class C change**, scoped to the two
  chapters, and **last**: its frontmatter denies edits under `src/` and
  `tests/` for the rest of the turn. Run it from a subagent, and treat a
  finding outside this PR's paragraphs as somebody else's branch.
- **Task 3 and Task 5 need a running Docker daemon.** Check `docker info`
  first. A container test is never skipped: without a daemon it fails on
  `Failed to connect to Docker endpoint`, and that is not a result.
- Every step that adds behaviour writes its test first and sees it fail.

## Review Focus

Five inputs the spec implies, each likely to bite and none exercised by an
obvious test. Each is pinned by a test in the task that owns the code.

1. **A command the serialiser refuses.** `System.Text.Json` throws
   `NotSupportedException` on a `System.Type` member. Thrown after
   `TryClaimAsync`, that would leave the key held in progress for the whole
   retention with nothing to release it. The fingerprint is therefore computed
   before the claim. Pinned in Task 2 by
   `A_command_that_cannot_be_fingerprinted_claims_nothing`, which asserts the
   store saw no call at all.
2. **A result with no value.** `Replay`'s `ValueType is null` guard returns
   without reading the payload, so a comparison placed after it would replay a
   void command to any request whatever. The stored shape is
   `sha256:{fingerprint}:null`. Pinned in Task 2 by
   `A_void_command_is_stored_behind_its_fingerprint_and_replays` and
   `A_void_entry_under_another_fingerprint_is_refused_though_it_has_no_value_to_read`.
   PR-B's `ReinstateReservationCommand` returns `Result`, so this is the path
   it takes.
3. **The envelope's edges.** A value whose JSON holds a `:` — any DTO, or a
   string such as `urn:order:1` — must survive, so the envelope is stripped by
   its length and never split on the separator. A previous release's
   `Result<string>` whose value begins `sha256:` is stored as `"sha256:…"`,
   opening with a quote, and must replay as a bare value. Pinned in Task 2 by
   `A_result_whose_JSON_holds_a_colon_survives_the_envelope` and
   `A_previous_release_s_string_that_spells_the_prefix_is_still_a_bare_value`.
4. **A stored fingerprint that is nearly this command's.** Upper-case hex, a
   truncated digest and a digest with no separator after it are each refused
   as reused: the comparison is ordinal over the whole envelope, and an
   envelope the behaviour did not write is never read as a match, never
   re-run and never a 500. Pinned in Task 2 by the three cases of
   `A_stored_fingerprint_that_is_not_this_commands_exactly_is_refused`.
5. **What the same command means across a body and across a deploy.** A
   reordered list of lines and `10.0` for `10` are different requests; a
   defaulted optional field is not; an entry the previous release wrote
   (`null`, a quoted GUID) carries no prefix and replays. Pinned in Task 2 by
   `A_reordered_list_is_a_different_request`,
   `A_decimal_keeps_the_scale_it_was_sent_with`,
   `A_defaulted_optional_field_hashes_as_its_absence_does`,
   `An_entry_the_previous_release_wrote_replays_with_no_fingerprint_to_compare`
   and the three existing tests that plant the old shapes, kept unchanged. The
   other direction of the rolling deploy — a replica on the previous release
   meeting the new payload — cannot be staged from this tree; ADR-057's
   *Consequences* state it.

## File Structure

| File | | Responsibility |
|---|---|---|
| `src/BuildingBlocks/Common.Application/CommandFingerprint.cs` | Create | The SHA-256 of a command as the pipeline holds it, defaults omitted |
| `src/BuildingBlocks/Common.Application/CommandIdReusedException.cs` | Create | The refusal: a completed key sent again with a different command |
| `src/BuildingBlocks/Common.Application/IdempotencyBehavior.cs` | Modify | Fingerprints before the claim; `Capture` writes the envelope; `Replay` compares and strips it |
| `src/BuildingBlocks/Common.Web/CommandIdReusedExceptionHandler.cs` | Create | 409, `code` `command.id_reused`, no key in the body |
| `src/BuildingBlocks/Common.Web/ProblemDetailsExtensions.cs` | Modify | Registers the handler beside the other 409 handlers |
| `tests/Common.Application.Tests/CommandFingerprintTests.cs` | Create | What makes two requests the same request, read off the stored payload |
| `tests/Common.Application.Tests/IdempotencyBehaviorTests.cs` | Modify | The held-key table, the envelope's edges, and the two test commands with content |
| `tests/Common.Web.Tests/CommandIdReusedExceptionHandlerTests.cs` | Create | The 409 on the wire |
| `tests/Common.Web.Tests/CommandAlreadyCommittedExceptionHandlerTests.cs` | Modify | The one test that pins the 409 codes as a distinct set gains the fourth |
| `tests/Ordering.Api.Tests/PlaceOrderTests.cs` | Modify | The wiring: one `CommandId`, two baskets, one order |
| `docs/backend-architecture/adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md` | Create | The rule, why, and what it costs |
| `docs/backend-architecture/appendix-a-adrs.md` | Modify | The ADR's index row |
| `docs/backend-architecture/08-caching-redis.md` | Modify | §8.5's behaviour sample and the callout that states the rule |
| `docs/backend-architecture/10-api-gateway.md` | Modify | §10.5's registration sample, its table row, and the two paragraphs that count and name the 409 codes |

**Existing tests that plant or assert a stored payload**, found by searching
`tests/` for `Completed(`, `Payload`, `StringSetAsync` and `"null"`, and what
each needs:

| Site | What it does | Verdict |
|---|---|---|
| `IdempotencyBehaviorTests.cs:39` | Asserts the stored payload is exactly the quoted GUID | **Edited** in Task 2: the payload now opens with the prefix |
| `IdempotencyBehaviorTests.cs:77`, `:390` | Plant a quoted GUID and expect a replay | Unchanged, and now the previous-release case for `Result<Guid>` |
| `IdempotencyBehaviorTests.cs:98` | Plants `null` and expects a void replay | Unchanged, and now the previous-release case for `Result` |
| `Common.Infrastructure.Tests/RedisIdempotencyStoreTests.cs` (every `CompleteAsync`, `StringSetAsync` and `Payload` site) | Store-level: the payload is an opaque string to the store | Unchanged; the file is not touched |
| `RetentionPurgeTests.cs` in five services | Decorators that forward `CompleteAsync` | Unchanged |
| `Catalog.Api.Tests/ProductEndpointsTests.cs:72` | One command twice over HTTP and a real Redis, expecting a replay | Unchanged; it is the control for Task 3's refusal and is run there |
| `IdempotencyMarkerTests.cs` in Ordering and Catalog | Drive `TransactionBehavior` with a key on the context; never read the claim store | Unchanged |
| `ConcurrentRequestExceptionHandlerTests.cs`, `ProblemDetailsCompositionTests.cs` | Assert the other handlers and the shared customisation | Unchanged |
| `CommandAlreadyCommittedExceptionHandlerTests.cs:82` | Pins three 409 codes as distinct | **Edited** in Task 3: it gains the fourth and loses the count in its name |

---

### Task 1: The branch, the number and a green baseline

**Files:** none.

**Interfaces:**
- Consumes: a clean `main` and `/branch`.
- Produces: a worktree under `.claude/worktrees/` on a `feat(common)/…`
  branch, and the ADR number every later task uses.

- [ ] **Step 1: Confirm `main` is clean**

Run: `git status --short`
Expected: no output. If `docs/superpowers/` files are listed as untracked,
stop: they are a `docs:` PR of their own (Global Constraints), and cutting the
branch now would carry them into a Class C diff.

- [ ] **Step 2: Cut the branch**

Run: `/branch feat(common): a command id is bound to the fingerprint of the command that claimed it`
Expected: a report naming a worktree under `.claude/worktrees/` and a branch
whose name begins `feat(common)/`. Then:

Run: `git branch --show-current`
Expected: that branch's name, and not `main`.

- [ ] **Step 3: Confirm the ADR number**

Run: `ls docs/backend-architecture/adr/ | tail -1`
Expected:
`ADR-056-a-services-fixture-derives-from-one-shared-body-under-tests.md`. If
the last file is numbered 057 or higher, apply the substitution Global
Constraints describes before going on.

- [ ] **Step 4: See the baseline green**

Run: `dotnet build Platform.slnx`
Expected: `0 Warning(s)` and `0 Error(s)`.

Run: `dotnet test tests/Common.Application.Tests`
Expected: `Passed!`, with `Failed: 0` (99 tests at `b07cd0b2`).

Run: `dotnet test tests/Common.Web.Tests`
Expected: `Passed!`, with `Failed: 0` (206 tests at `b07cd0b2`).

Nothing is committed in this task.

---

### Task 2: The fingerprint, stored beside the result and compared on a retry

**Files:**
- Create: `tests/Common.Application.Tests/CommandFingerprintTests.cs`
- Modify: `tests/Common.Application.Tests/IdempotencyBehaviorTests.cs:1-3`
  (usings), `:39` (the payload assertion), `:402` (new tests and helpers,
  inserted above `ExpectedKey`), `:446` (two test commands, inserted above
  `UnprotectedCommand`'s summary)
- Create: `src/BuildingBlocks/Common.Application/CommandIdReusedException.cs`
- Create: `src/BuildingBlocks/Common.Application/CommandFingerprint.cs`
- Modify: `src/BuildingBlocks/Common.Application/IdempotencyBehavior.cs`
  (whole file reprinted; the changes are at `:18-19`, `:36-48`, `:80` and
  `:87-101`)
- Test: both test files above

**Interfaces:**
- Consumes: `IIdempotencyStore`, `IdempotencyEntry`, `IdempotencyContext`,
  `ICurrentUser`, `ConcurrentRequestException` and `Result` from
  `Common.Application`, unchanged; from `IdempotencyDoubles.cs`,
  `RecordingIdempotencyStore` (`Entries`, `Calls`, `Completed(key, payload)`,
  `InFlight(key)`) and `StubCurrentUser` (`Authenticated(id)`, `Anonymous()`),
  unchanged.
- Produces:

```csharp
namespace Common.Application;

internal static class CommandFingerprint
{
    public static string Of<TCommand>(TCommand command);
}

public sealed class CommandIdReusedException(Guid commandId) : Exception
{
    public Guid CommandId { get; }
}
```

  and the stored payload's shape, `sha256:{64 lower-case hex digits}:{json}`,
  where `{json}` is what the behaviour stored before this change. Task 3
  consumes `CommandIdReusedException`. The test project gains
  `ContentCommand(Guid CommandId, string Content) : ICommand<Result<string>>`
  and `UnserialisableCommand(Guid CommandId, Type Shape) : ICommand<Result>`,
  both `IIdempotentCommand`.

- [ ] **Step 1: Write the failing fingerprint suite**

Create `tests/Common.Application.Tests/CommandFingerprintTests.cs`, then
`unix2dos` it:

```csharp
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>ADR-057's fingerprint: what makes two requests under one <c>CommandId</c> the same request.</summary>
/// <remarks>Read off the stored payload, as a retry meets it: <c>CommandFingerprint</c> is internal (§8.5).</remarks>
public class CommandFingerprintTests
{
    private static readonly Guid Command = Guid.Parse("0195e4b2-0000-7000-8000-0000000000ff");
    private static readonly Guid Desk = Guid.Parse("0195e4b2-0000-7000-8000-0000000000d1");
    private static readonly Guid Lamp = Guid.Parse("0195e4b2-0000-7000-8000-0000000000d2");

    private sealed record Bare(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "tests.fingerprint.bare";
    }

    private sealed record Line(Guid ProductId, int Quantity);

    private sealed record Basket(Guid CommandId, IReadOnlyList<Line> Lines, decimal Total, string Currency)
        : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "tests.fingerprint.basket";
    }

    /// <summary><see cref="Basket"/> one release later, with an optional field appended.</summary>
    private sealed record BasketWithNote(
        Guid CommandId,
        IReadOnlyList<Line> Lines,
        decimal Total,
        string Currency,
        string? Note = null) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "tests.fingerprint.basket";
    }

    private static Basket TwoDesksAndALamp() => new(Command, [new Line(Desk, 2), new Line(Lamp, 1)], 149.90m, "EUR");

    [Fact]
    public async Task The_fingerprint_is_the_SHA_256_of_the_commands_JSON_in_lower_case_hex()
    {
        // The JSON is spelled out, so a naming policy or a GUID format added to the options fails here.
        string json = $$"""{"CommandId":"{{Command}}"}""";
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        string fingerprint = await FingerprintOfAsync(new Bare(Command));

        fingerprint.ShouldBe(expected);
        fingerprint.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Equal_commands_hash_equal_though_they_are_two_instances()
    {
        // The two are not Equals-equal, since a record compares a list by reference; their content is.
        (await FingerprintOfAsync(TwoDesksAndALamp())).ShouldBe(await FingerprintOfAsync(TwoDesksAndALamp()));
    }

    [Fact]
    public async Task One_changed_field_hashes_different()
    {
        string original = await FingerprintOfAsync(TwoDesksAndALamp());

        (await FingerprintOfAsync(TwoDesksAndALamp() with { Currency = "USD" })).ShouldNotBe(original);
        (await FingerprintOfAsync(TwoDesksAndALamp() with { Total = 149.91m })).ShouldNotBe(original);
        (await FingerprintOfAsync(TwoDesksAndALamp() with { CommandId = Guid.CreateVersion7() }))
            .ShouldNotBe(original);
    }

    [Fact]
    public async Task A_change_inside_a_nested_line_hashes_different()
    {
        Basket threeDesks = TwoDesksAndALamp() with { Lines = [new Line(Desk, 3), new Line(Lamp, 1)] };

        (await FingerprintOfAsync(threeDesks)).ShouldNotBe(await FingerprintOfAsync(TwoDesksAndALamp()));
    }

    [Fact]
    public async Task A_reordered_list_is_a_different_request()
    {
        // ADR-057's residual, pinned: the fingerprint is of the bound command, and a list binds in order.
        Basket reordered = TwoDesksAndALamp() with { Lines = [new Line(Lamp, 1), new Line(Desk, 2)] };

        (await FingerprintOfAsync(reordered)).ShouldNotBe(await FingerprintOfAsync(TwoDesksAndALamp()));
    }

    [Fact]
    public async Task A_decimal_keeps_the_scale_it_was_sent_with()
    {
        // The same residual: 10 and 10.0 bind to two decimals, so a retry re-sends the bytes it sent.
        (await FingerprintOfAsync(TwoDesksAndALamp() with { Total = 10m }))
            .ShouldNotBe(await FingerprintOfAsync(TwoDesksAndALamp() with { Total = 10.0m }));
    }

    [Fact]
    public async Task A_defaulted_optional_field_hashes_as_its_absence_does()
    {
        // What lets a retry straddle the deploy that added the field (ADR-057).
        Basket before = TwoDesksAndALamp();
        BasketWithNote after = new(before.CommandId, before.Lines, before.Total, before.Currency);

        (await FingerprintOfAsync(after)).ShouldBe(await FingerprintOfAsync(before));
        (await FingerprintOfAsync(after with { Note = "Leave with the porter." }))
            .ShouldNotBe(await FingerprintOfAsync(before));
    }

    /// <summary>The fingerprint a retry is compared with: the one stored beside the first attempt's result.</summary>
    private static async Task<string> FingerprintOfAsync<TCommand>(TCommand command)
        where TCommand : ICommand<Result>, IIdempotentCommand
    {
        const string prefix = "sha256:";
        const string noValue = ":null";

        RecordingIdempotencyStore store = new();
        IdempotencyBehavior<TCommand, Result> behaviour =
            new(store, StubCurrentUser.Anonymous(), new IdempotencyContext());

        await behaviour.HandleAsync(
            command,
            () => Task.FromResult(Result.Success()),
            TestContext.Current.CancellationToken);

        string payload = store.Entries.Values.ShouldHaveSingleItem().Payload.ShouldNotBeNull();
        payload.ShouldStartWith(prefix, Case.Sensitive);
        payload.ShouldEndWith(noValue, Case.Sensitive);

        return payload[prefix.Length..^noValue.Length];
    }
}
```

`CommandFingerprint` is internal and this suite never names it: every test
dispatches through `IdempotencyBehavior` and reads the digest out of the
payload the store was handed, which is the value a retry is compared with.

- [ ] **Step 2: Run it and see it fail**

Run: `dotnet test tests/Common.Application.Tests --filter "FullyQualifiedName~CommandFingerprintTests"`
Expected: `Failed!  - Failed: 7, Passed: 0`, each on

```
Shouldly.ShouldAssertException : payload
    should start with
"sha256:"
    but was
"null"
```

The behaviour still stores the bare value.

- [ ] **Step 3: Write the failing behaviour tests**

Four edits to `tests/Common.Application.Tests/IdempotencyBehaviorTests.cs`,
made with the Edit tool. The prototype's edits kept the file's CRLF; confirm
with `file` when the four are in.

Edit 1 — the usings. Replace:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
```

with:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
```

Edit 2 — the stored payload, in
`A_first_attempt_claims_the_key_runs_the_handler_and_records_the_outcome`.
Replace:

```csharp
        store.Entries[ExpectedKey].ShouldBe(new IdempotencyEntry(false, $"\"{placed}\""));
```

with:

```csharp
        store.Entries[ExpectedKey].ShouldBe(
            new IdempotencyEntry(false, $"sha256:{FingerprintOf(BareJson)}:\"{placed}\""),
            "the value is stored behind the fingerprint of the command that produced it (ADR-057)");
```

Edit 3 — the new tests and their helpers. Replace this one line, which sits
below the last test:

```csharp
    private static string ExpectedKey => $"{Caller}:{ProtectedCommand.OperationName}:{Command}";
```

with:

```csharp
    [Fact]
    public async Task A_retry_carrying_the_same_command_replays_the_first_result()
    {
        RecordingIdempotencyStore store = new();
        int handlerRuns = 0;

        Task<Result<string>> Handler()
        {
            handlerRuns++;
            return Task.FromResult(Result.Success($"order-{handlerRuns}"));
        }

        Result<string> first = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            Handler,
            TestContext.Current.CancellationToken);

        Result<string> second = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            Handler,
            TestContext.Current.CancellationToken);

        handlerRuns.ShouldBe(1, "an equal command under a completed key is the first one repeated");
        second.Value.ShouldBe(first.Value);
        store.Calls.ShouldBe(
            [$"claim {ContentKey}", $"complete {ContentKey}", $"claim {ContentKey}", $"get {ContentKey}"]);
    }

    [Fact]
    public async Task A_retry_carrying_a_different_command_is_refused_and_the_handler_does_not_run()
    {
        RecordingIdempotencyStore store = new();
        int handlerRuns = 0;

        Task<Result<string>> Handler()
        {
            handlerRuns++;
            return Task.FromResult(Result.Success("order-1"));
        }

        await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            Handler,
            TestContext.Current.CancellationToken);

        IdempotencyEntry recorded = store.Entries[ContentKey];

        CommandIdReusedException thrown = await Should.ThrowAsync<CommandIdReusedException>(
            () => Content(store).HandleAsync(
                new ContentCommand(Command, "three desks"),
                Handler,
                TestContext.Current.CancellationToken));

        thrown.CommandId.ShouldBe(Command);
        handlerRuns.ShouldBe(1, "a 200 carrying the first request's result would say the second was applied");
        store.Entries[ContentKey].ShouldBe(recorded, "the refusal leaves the first request's entry as it found it");
        store.Calls[^2..].ShouldBe([$"claim {ContentKey}", $"get {ContentKey}"], "neither a release nor a write");
    }

    [Fact]
    public async Task An_entry_the_previous_release_wrote_replays_with_no_fingerprint_to_compare()
    {
        // The shape before ADR-057: the bare value, which a rolling deploy leaves live for the claim's window.
        RecordingIdempotencyStore store = new();
        store.Completed(ContentKey, "\"order-1\"");

        Result<string> result = await Content(store).HandleAsync(
            new ContentCommand(Command, "whatever the first request carried"),
            () => throw new InvalidOperationException("the handler must not run on a replay"),
            TestContext.Current.CancellationToken);

        result.Value.ShouldBe("order-1");
    }

    [Fact]
    public async Task A_previous_release_s_string_that_spells_the_prefix_is_still_a_bare_value()
    {
        // A JSON string opens with a quote, so the prefix is matched at the payload's first character only.
        RecordingIdempotencyStore store = new();
        store.Completed(ContentKey, "\"sha256:not-a-fingerprint\"");

        Result<string> result = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            () => throw new InvalidOperationException("the handler must not run on a replay"),
            TestContext.Current.CancellationToken);

        result.Value.ShouldBe("sha256:not-a-fingerprint");
    }

    [Fact]
    public async Task A_result_whose_JSON_holds_a_colon_survives_the_envelope()
    {
        // The envelope is stripped by its length, never by splitting on the separator.
        RecordingIdempotencyStore store = new();

        await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            () => Task.FromResult(Result.Success("urn:order:1")),
            TestContext.Current.CancellationToken);

        Result<string> replayed = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            () => throw new InvalidOperationException("the handler must not run on a replay"),
            TestContext.Current.CancellationToken);

        replayed.Value.ShouldBe("urn:order:1");
    }

    [Fact]
    public async Task A_void_command_is_stored_behind_its_fingerprint_and_replays()
    {
        RecordingIdempotencyStore store = new();
        int handlerRuns = 0;

        Task<Result> Handler()
        {
            handlerRuns++;
            return Task.FromResult(Result.Success());
        }

        await Void(store).HandleAsync(new VoidProtectedCommand(Command), Handler, TestContext.Current.CancellationToken);

        Result replayed = await Void(store).HandleAsync(
            new VoidProtectedCommand(Command),
            Handler,
            TestContext.Current.CancellationToken);

        store.Entries[VoidKey].Payload.ShouldBe($"sha256:{FingerprintOf(BareJson)}:null");
        replayed.IsSuccess.ShouldBeTrue();
        handlerRuns.ShouldBe(1);
    }

    [Fact]
    public async Task A_void_entry_under_another_fingerprint_is_refused_though_it_has_no_value_to_read()
    {
        // The comparison runs before the no-value shortcut, or every void command would replay any request.
        RecordingIdempotencyStore store = new();
        store.Completed(VoidKey, $"sha256:{new string('0', 64)}:null");

        await Should.ThrowAsync<CommandIdReusedException>(
            () => Void(store).HandleAsync(
                new VoidProtectedCommand(Command),
                () => throw new InvalidOperationException("the handler must not run under a held key"),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(nameof(Stored.UpperCase))]
    [InlineData(nameof(Stored.Truncated))]
    [InlineData(nameof(Stored.Unseparated))]
    public async Task A_stored_fingerprint_that_is_not_this_commands_exactly_is_refused(string stored)
    {
        // Ordinal, and fail-closed: an envelope this behaviour did not write is never read as a match.
        ContentCommand command = new(Command, "two desks");
        string fingerprint = FingerprintOf($$"""{"CommandId":"{{Command}}","Content":"two desks"}""");

        string payload = stored switch
        {
            nameof(Stored.UpperCase) => $"sha256:{fingerprint.ToUpperInvariant()}:\"order-1\"",
            nameof(Stored.Truncated) => $"sha256:{fingerprint[..32]}:\"order-1\"",
            _ => $"sha256:{fingerprint}\"order-1\""
        };

        RecordingIdempotencyStore store = new();
        store.Completed(ContentKey, payload);

        await Should.ThrowAsync<CommandIdReusedException>(
            () => Content(store).HandleAsync(
                command,
                () => throw new InvalidOperationException("the handler must not run under a held key"),
                TestContext.Current.CancellationToken));
    }

    private enum Stored
    {
        UpperCase,
        Truncated,
        Unseparated
    }

    [Fact]
    public async Task An_in_flight_duplicate_carrying_a_different_command_is_still_told_to_retry()
    {
        // The fingerprint is recorded with the outcome, so there is nothing to compare until one exists (ADR-057).
        RecordingIdempotencyStore store = new();
        store.InFlight(ContentKey);

        await Should.ThrowAsync<ConcurrentRequestException>(
            () => Content(store).HandleAsync(
                new ContentCommand(Command, "three desks"),
                () => throw new InvalidOperationException("the handler must not run under a held key"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_command_that_cannot_be_fingerprinted_claims_nothing()
    {
        // System.Text.Json refuses a System.Type; thrown after the claim, that would hold the key for the window.
        RecordingIdempotencyStore store = new();

        IdempotencyBehavior<UnserialisableCommand, Result> behaviour =
            new(store, StubCurrentUser.Authenticated(Caller), new IdempotencyContext());

        await Should.ThrowAsync<NotSupportedException>(
            () => behaviour.HandleAsync(
                new UnserialisableCommand(Command, typeof(string)),
                () => Task.FromResult(Result.Success()),
                TestContext.Current.CancellationToken));

        store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_command_stores_nothing_so_its_id_may_carry_a_corrected_request()
    {
        RecordingIdempotencyStore store = new();

        await Content(store).HandleAsync(
            new ContentCommand(Command, "no desks"),
            () => Task.FromResult(Result.Failure<string>(Error.Rule("test.refused", "No."))),
            TestContext.Current.CancellationToken);

        Result<string> corrected = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            () => Task.FromResult(Result.Success("order-1")),
            TestContext.Current.CancellationToken);

        corrected.Value.ShouldBe("order-1");
    }

    private static IdempotencyBehavior<ContentCommand, Result<string>> Content(RecordingIdempotencyStore store) =>
        new(store, StubCurrentUser.Authenticated(Caller), new IdempotencyContext());

    private static IdempotencyBehavior<VoidProtectedCommand, Result> Void(RecordingIdempotencyStore store) =>
        new(store, StubCurrentUser.Authenticated(Caller), new IdempotencyContext());

    /// <summary>A command carrying its <c>CommandId</c> and nothing else, as the fingerprint serialises it.</summary>
    private static string BareJson => $$"""{"CommandId":"{{Command}}"}""";

    /// <summary>ADR-057's fingerprint of a command whose JSON the test spells out, so the hashed shape is pinned.</summary>
    private static string FingerprintOf(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static string ExpectedKey => $"{Caller}:{ProtectedCommand.OperationName}:{Command}";

    private static string ContentKey => $"{Caller}:{ContentCommand.OperationName}:{Command}";
```

Edit 4 — the two test commands, at the foot of the file. Replace:

```csharp
/// <summary>Satisfies the behaviour's result constraint but not <see cref="IIdempotentCommand"/>.</summary>
```

with:

```csharp
/// <summary>An opted-in command with content, so two requests can share a <c>CommandId</c> and differ.</summary>
public sealed record ContentCommand(Guid CommandId, string Content) : ICommand<Result<string>>, IIdempotentCommand
{
    public static string OperationName => "tests.content";
}

/// <summary>An opted-in command <c>System.Text.Json</c> refuses to serialise.</summary>
public sealed record UnserialisableCommand(Guid CommandId, Type Shape) : ICommand<Result>, IIdempotentCommand
{
    public static string OperationName => "tests.unserialisable";
}

/// <summary>Satisfies the behaviour's result constraint but not <see cref="IIdempotentCommand"/>.</summary>
```

The three tests that plant a previous-release payload —
`A_retry_of_a_completed_command_replays_the_value_without_running_the_handler`,
`A_void_command_replays_a_success_carrying_no_value` and
`A_replay_publishes_no_key_because_it_opens_no_transaction` — are left exactly
as they are: a quoted GUID and `null` are the two shapes the previous release
wrote, and they must go on replaying.

- [ ] **Step 4: Run and see the compile failure**

Run: `dotnet test tests/Common.Application.Tests`
Expected: the build fails with
`error CS0246: The type or namespace name 'CommandIdReusedException' could not be found`.

- [ ] **Step 5: Add the exception**

Create `src/BuildingBlocks/Common.Application/CommandIdReusedException.cs`,
then `unix2dos` it:

```csharp
namespace Common.Application;

/// <summary>A key whose command completed was sent again carrying a different command (ADR-057).</summary>
/// <remarks>An exception, not an <see cref="Error"/>: no domain decided anything (§10.5).</remarks>
public sealed class CommandIdReusedException(Guid commandId)
    : Exception($"Command {commandId} was already used for a different request.")
{
    /// <summary>The reused <c>CommandId</c>, for the log line and nothing else.</summary>
    public Guid CommandId { get; } = commandId;
}
```

- [ ] **Step 6: Run and see the behaviour fail**

Run: `dotnet test tests/Common.Application.Tests`
Expected: `Failed!  - Failed: 15, Passed: 104` at `b07cd0b2`. The fifteen are
the seven of `CommandFingerprintTests` and, in `IdempotencyBehaviorTests`:

- `A_first_attempt_claims_the_key_runs_the_handler_and_records_the_outcome`
  and `A_void_command_is_stored_behind_its_fingerprint_and_replays` — the
  payload has no prefix;
- `A_retry_carrying_a_different_command_is_refused_and_the_handler_does_not_run`
  and `A_void_entry_under_another_fingerprint_is_refused_though_it_has_no_value_to_read`
  — nothing is thrown, because the first result is replayed;
- the three cases of
  `A_stored_fingerprint_that_is_not_this_commands_exactly_is_refused` — a
  `JsonException` where a `CommandIdReusedException` is expected;
- `A_command_that_cannot_be_fingerprinted_claims_nothing` — nothing is
  thrown, because nothing serialises the command.

The other new tests pass already, and that is correct: they pin behaviour
this change must keep — an equal command replays, a previous-release entry
replays, an in-flight duplicate is told to retry, a refused command stores
nothing.

- [ ] **Step 7: Add the fingerprint**

Create `src/BuildingBlocks/Common.Application/CommandFingerprint.cs`, then
`unix2dos` it:

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Application;

/// <summary>The SHA-256 of a command as the pipeline sees it, which §8.5 stores beside its result.</summary>
/// <remarks>Defaults are omitted, so a new optional field leaves an old request's fingerprint alone (ADR-057).</remarks>
internal static class CommandFingerprint
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public static string Of<TCommand>(TCommand command) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, Options)));
}
```

The options change one thing and nothing else: no naming policy, no
converter, no encoder. `CommandFingerprintTests`' first test spells the JSON
out so that a second change fails there.

- [ ] **Step 8: Teach the behaviour to store and compare it**

Replace the whole of
`src/BuildingBlocks/Common.Application/IdempotencyBehavior.cs` with the
following, then `unix2dos` it. What moved: the `FingerprintPrefix` constant;
the fingerprint computed between the key and the claim; `Replay` and `Capture`
each taking it; `Envelope`; and the comparison at the head of `Replay`.

```csharp
using System.Reflection;
using System.Text.Json;

namespace Common.Application;

/// <summary>§8.5's claim-before-work exclusion, between validation and the transaction (§6.3).</summary>
/// <remarks>A command that does not opt in, or returns no <see cref="Result"/>, is unprotected (§8.5).</remarks>
public sealed class IdempotencyBehavior<TCommand, TResult>(
    IIdempotencyStore store,
    ICurrentUser currentUser,
    IdempotencyContext idempotency)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>, IIdempotentCommand
    where TResult : Result
{
    private static readonly TimeSpan Retention = IdempotencyRetention.Window;

    // "null", not the empty string, which a store is likeliest to read as an absent payload.
    private const string NoValue = "null";

    // Opens a payload that carries a fingerprint; no JSON value begins with "s", so no bare value spells it.
    private const string FingerprintPrefix = "sha256:";

    // Resolved once per closed (TCommand, TResult), in declaration order; Result and Result<T> are the only shapes.
    private static readonly Type? ValueType = ValueTypeOf();

    private static readonly PropertyInfo? ValueProperty =
        ValueType is null ? null : typeof(TResult).GetProperty(nameof(Result<object>.Value));

    private static readonly MethodInfo? SuccessOfValue = ValueType is null
        ? null
        : typeof(Result)
            .GetMethod(nameof(Result.Success), 1, [Type.MakeGenericMethodParameter(0)])!
            .MakeGenericMethod(ValueType);

    public async Task<TResult> HandleAsync(TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // The store owns the prefix; the subject segment stops one caller naming another's key (§8.5).
        string key = $"{Subject()}:{TCommand.OperationName}:{command.CommandId}";

        // Before the claim, so a command that cannot be serialised holds no key (ADR-057).
        string fingerprint = CommandFingerprint.Of(command);

        // The token makes a write from an expired claim a no-op rather than a clobber of its successor's.
        string? claim = await store.TryClaimAsync(key, Retention, ct);

        if (claim is null)
        {
            IdempotencyEntry? existing = await store.GetAsync(key, ct);

            if (existing is null || existing.InProgress)
                throw new ConcurrentRequestException(command.CommandId);

            return Replay(existing.Payload!, fingerprint, command.CommandId);
        }

        // Set after the claim, so a command about to replay hands §6.3 no key.
        idempotency.Claim(key);

        TResult result;

        try
        {
            result = await next();
        }
        catch
        {
            // Released even if the commit landed unacknowledged: the retry then meets §6.3's marker.
            await store.ReleaseAsync(key, claim, CancellationToken.None);
            throw;
        }
        finally
        {
            // A scope may dispatch twice, so the key lives for this dispatch only.
            idempotency.Clear();
        }

        if (result.IsFailure)
        {
            // A refusal commits nothing, so there is nothing to replay.
            await store.ReleaseAsync(key, claim, CancellationToken.None);
            return result;
        }

        // No retention: the claim's window runs from the claim, not the commit (ADR-038).
        await store.CompleteAsync(key, claim, Capture(result, fingerprint), CancellationToken.None);
        return result;
    }

    // Bound from the principal (§11.4); every unauthenticated caller shares one segment, a residual §8.5 argues.
    private string Subject() => currentUser.IsAuthenticated ? currentUser.Id.ToString() : "system";

    // Only a success's value is stored, since a Result survives no JSON round trip (§8.5).
    private static string Capture(TResult result, string fingerprint)
    {
        string value = ValueType is null
            ? NoValue
            : JsonSerializer.Serialize(ValueProperty!.GetValue(result), ValueType);

        return Envelope(fingerprint) + value;
    }

    private static string Envelope(string fingerprint) => $"{FingerprintPrefix}{fingerprint}:";

    private static TResult Replay(string payload, string fingerprint, Guid commandId)
    {
        // Compared before any value is read; an unprefixed entry is the previous release's and replays (ADR-057).
        if (payload.StartsWith(FingerprintPrefix, StringComparison.Ordinal))
        {
            string envelope = Envelope(fingerprint);

            if (!payload.StartsWith(envelope, StringComparison.Ordinal))
                throw new CommandIdReusedException(commandId);

            payload = payload[envelope.Length..];
        }

        // The guard is required: the cast compiles for every TResult and fails at run time for all but Result.
        if (ValueType is null)
            return (TResult)Result.Success();

        object? value = JsonSerializer.Deserialize(payload, ValueType);
        return (TResult)SuccessOfValue!.Invoke(null, [value])!;
    }

    private static Type? ValueTypeOf()
    {
        if (typeof(TResult) == typeof(Result))
            return null;

        if (typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(Result<>))
            return typeof(TResult).GetGenericArguments()[0];

        // Unreachable while Result<T> is sealed and Result's constructor is private protected.
        throw new NotSupportedException(
            $"{typeof(TResult).Name} is neither Result nor Result<T>, so no stored outcome " +
            "can be rebuilt for it. A third Result shape is a change to this behaviour.");
    }
}
```

The comparison is two `StartsWith` calls and no index arithmetic: a payload
that does not open with the prefix is the previous release's; one that opens
with the prefix but not with this command's whole envelope — another digest,
a digest in another case, a truncated one, one with no separator — is a
different command's.

- [ ] **Step 9: Run and see everything pass**

Run: `dotnet test tests/Common.Application.Tests`
Expected: `Passed!  - Failed: 0, Passed: 119` at `b07cd0b2`.

Run: `dotnet build Platform.slnx`
Expected: `0 Warning(s)` and `0 Error(s)` — every service compiles against
the behaviour, so this is where a broken signature would show.

- [ ] **Step 10: Commit**

```bash
git branch --show-current
git add src/BuildingBlocks/Common.Application/CommandFingerprint.cs \
    src/BuildingBlocks/Common.Application/CommandIdReusedException.cs \
    src/BuildingBlocks/Common.Application/IdempotencyBehavior.cs \
    tests/Common.Application.Tests/CommandFingerprintTests.cs \
    tests/Common.Application.Tests/IdempotencyBehaviorTests.cs
git commit -F - <<'EOF'
feat(common): IdempotencyBehavior stores a result behind its command's fingerprint

Class C. Touch set: src/BuildingBlocks/Common.Application/**,
src/BuildingBlocks/Common.Web/**, tests/Common.Application.Tests/**,
tests/Common.Web.Tests/**, tests/Ordering.Api.Tests/PlaceOrderTests.cs,
ADR-057's file, appendix-a-adrs.md, 08-caching-redis.md and
10-api-gateway.md.

The key was bound to a subject and an operation and to nothing the request
said, so a second, different request under a CommandId already used was
answered 200 with the first request's result. CommandFingerprint.Of hashes
the command as the pipeline holds it, before the claim, and Capture writes
the value behind it as sha256:{fingerprint}:{json}. Replay compares before
it reads: an equal fingerprint replays, a different one throws
CommandIdReusedException without running the handler, and a payload with no
prefix is the previous release's and replays as it stands.

The port, both Redis scripts and the marker row are unchanged, so no service
migrates. The cost is that an idempotent command's shape becomes a
compatibility surface: removing or renaming a field refuses a retry that
straddles the deploy. CommandFingerprint stays internal and the suite reads
the digest off the stored payload, as it reaches the internal Dispatcher
through the container, so no project file changes. The generic
SerializeToUtf8Bytes overload is used because CA2263 fails the build on the
Type-taking one.
EOF
```

The first line of output must be the branch from Task 1 and not `main`.

---

### Task 3: The 409 `command.id_reused`, and the Ordering test that proves the wiring

**Files:**
- Modify: `tests/Ordering.Api.Tests/PlaceOrderTests.cs:1-3` (a using),
  `:174` (the new test, inserted above
  `A_malformed_request_is_a_400_before_the_domain_sees_it`), `:201-210`
  (`PlaceAsync` takes an optional `commandId`)
- Create: `tests/Common.Web.Tests/CommandIdReusedExceptionHandlerTests.cs`
- Modify: `tests/Common.Web.Tests/CommandAlreadyCommittedExceptionHandlerTests.cs:81-95`
- Create: `src/BuildingBlocks/Common.Web/CommandIdReusedExceptionHandler.cs`
- Modify: `src/BuildingBlocks/Common.Web/ProblemDetailsExtensions.cs:18`
- Test: the three test files above, and
  `tests/Catalog.Api.Tests/ProductEndpointsTests.cs` as the unchanged control

**Interfaces:**
- Consumes: `CommandIdReusedException(Guid commandId)` from Task 2;
  `AddCommonProblemDetails` and `IProblemDetailsService`; Ordering's
  `ServiceFixture` (`ResetAsync`, `ScalarAsync<T>`, `Factory`) and
  `TestAuthHandler`, unchanged.
- Produces:

```csharp
namespace Common.Web;

internal sealed class CommandIdReusedExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken);
}
```

  and on the wire: status 409, `application/problem+json`, `code`
  `command.id_reused`, no `CommandId` anywhere in the body, and this `detail`:

```
This command identifier was already used for a different request; send a changed request under a new identifier.
```

- [ ] **Step 1: Write the failing wiring test**

Confirm the daemon first.

Run: `docker info --format '{{.ServerVersion}}'`
Expected: a version number. An error means Docker Desktop is not running;
start it and wait for this command to answer before going on.

Three edits to `tests/Ordering.Api.Tests/PlaceOrderTests.cs`, with the Edit
tool, and `file` on it afterwards.

Edit 1 — the using. Replace:

```csharp
using System.Net.Http.Json;
```

with:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
```

Edit 2 — the test. Replace:

```csharp
    [Fact]
    public async Task A_malformed_request_is_a_400_before_the_domain_sees_it()
```

with:

```csharp
    [Fact]
    public async Task One_command_id_carrying_a_second_basket_is_refused_and_one_order_exists()
    {
        // ADR-057 through the registered pipeline and a real Redis: a 200 here would carry the first basket's
        // order id and tell the caller the second basket was placed.
        Guid desk = Guid.CreateVersion7();
        Guid lamp = Guid.CreateVersion7();
        await SeedPriceAsync(desk, 19.99m, "EUR");
        await SeedPriceAsync(lamp, 5m, "EUR");
        Guid commandId = Guid.CreateVersion7();

        HttpResponseMessage first = await PlaceAsync(desk, quantity: 2, commandId: commandId);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage second = await PlaceAsync(lamp, commandId: commandId);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using JsonDocument problem = JsonDocument.Parse(
            await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("command.id_reused");

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM ordering.Orders"))
            .ShouldBe(1, "the second basket never reached the handler");
    }

    [Fact]
    public async Task A_malformed_request_is_a_400_before_the_domain_sees_it()
```

Edit 3 — `PlaceAsync`. Replace:

```csharp
    /// <summary>A fresh <c>CommandId</c> per call, or a second order would replay the first's result (§8.5).</summary>
    private Task<HttpResponseMessage> PlaceAsync(Guid product, int quantity = 1, string currency = "EUR") =>
        Authenticated().PostAsJsonAsync(
            "/v1/orders",
            new PlaceOrderCommand(
                Guid.CreateVersion7(),
```

with:

```csharp
    /// <summary>A fresh <c>CommandId</c> unless the test pins one, since a repeated id is a retry (§8.5).</summary>
    private Task<HttpResponseMessage> PlaceAsync(
        Guid product,
        int quantity = 1,
        string currency = "EUR",
        Guid? commandId = null) =>
        Authenticated().PostAsJsonAsync(
            "/v1/orders",
            new PlaceOrderCommand(
                commandId ?? Guid.CreateVersion7(),
```

The caller is `PlaceOrderTests`' one pinned `Caller`, so both requests claim
under one subject and one key; `InitializeAsync` resets the database, so the
count is of this test's orders alone.

- [ ] **Step 2: Run it and see it fail on the missing translation**

Run: `dotnet test tests/Ordering.Api.Tests --filter "FullyQualifiedName~PlaceOrderTests.One_command_id_carrying_a_second_basket"`
Expected: `Failed: 1`, on

```
second.StatusCode
    should be
HttpStatusCode.Conflict
    but was
HttpStatusCode.InternalServerError
```

The behaviour from Task 2 already refuses the second basket; nothing yet
turns the exception into a response, so `UseExceptionHandler` answers 500.

- [ ] **Step 3: Write the failing handler suite**

Create `tests/Common.Web.Tests/CommandIdReusedExceptionHandlerTests.cs`, then
`unix2dos` it:

```csharp
using System.Net;
using System.Text.Json;
using Common.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>ADR-057's refusal on the wire, which unregistered would be a 500 a client retries into again.</summary>
public class CommandIdReusedExceptionHandlerTests
{
    private static readonly Guid CommandId = Guid.Parse("6f1d2a70-9c3b-4a1e-8f52-1b7c4d905e33");

    [Fact]
    public async Task A_reused_command_id_becomes_a_409()
    {
        using IHost host = await StartThrowingAsync(new CommandIdReusedException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task The_409_names_itself_with_a_code_a_client_can_switch_on()
    {
        using JsonDocument body = await BodyOfAsync(new CommandIdReusedException(CommandId));

        body.RootElement.GetProperty("code").GetString().ShouldBe("command.id_reused");
    }

    [Fact]
    public async Task The_409_carries_the_same_customisation_as_every_other_problem_response()
    {
        using JsonDocument body = await BodyOfAsync(new CommandIdReusedException(CommandId));

        body.RootElement.GetProperty("instance").GetString().ShouldBe("GET /orders");
        body.RootElement.TryGetProperty("traceId", out _).ShouldBeTrue();
        body.RootElement.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_detail_asks_for_a_new_identifier_and_does_not_say_retry()
    {
        using JsonDocument body = await BodyOfAsync(new CommandIdReusedException(CommandId));

        string detail = body.RootElement.GetProperty("detail").GetString()!;

        detail.ShouldContain("already used for a different request");
        detail.ShouldContain("new identifier");
        detail.ShouldNotContain("retry", Case.Insensitive, "a retry under this identifier meets the same refusal");
    }

    [Fact]
    public async Task The_body_does_not_echo_the_command_id()
    {
        using IHost host = await StartThrowingAsync(new CommandIdReusedException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The whole body, not the detail alone: the id is part of a key whose first segment is the subject (§8.5).
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain(CommandId.ToString());
    }

    [Fact]
    public async Task A_client_that_cannot_accept_problem_json_still_gets_the_409()
    {
        using IHost host = await StartThrowingAsync(new CommandIdReusedException(CommandId));
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Any_other_exception_still_falls_through_to_the_500()
    {
        // The handler selects: one matching everything would pass the tests above.
        using IHost host = await StartThrowingAsync(new InvalidOperationException("boom"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static async Task<JsonDocument> BodyOfAsync(Exception exception)
    {
        using IHost host = await StartThrowingAsync(exception);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        // The 500 fallback writes through the same service, so only the status shows this handler answered.
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static Task<IHost> StartThrowingAsync(Exception exception) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddCommonProblemDetails());
                web.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.Run(_ => throw exception);
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync();
}
```

Then, in
`tests/Common.Web.Tests/CommandAlreadyCommittedExceptionHandlerTests.cs`, the
test that pins the 409 codes as a set gains the fourth and loses the count in
its name. Replace:

```csharp
    public async Task The_three_409s_carry_distinct_machine_readable_codes()
    {
        // A client switches on §10.5's `code`, not on prose, so the codes are pinned.
        string committed = await CodeOfAsync(new CommandAlreadyCommittedException(Key));
        string inProgress = await CodeOfAsync(new ConcurrentRequestException(Guid.CreateVersion7()));
        string conflict = await CodeOfAsync(new DbUpdateConcurrencyException("stale"));

        committed.ShouldBe("command.already_committed");
        inProgress.ShouldBe("request.in_progress");
        conflict.ShouldBe("request.concurrency_conflict");

        // Distinct as a set, since two of the three would satisfy any pair of assertions.
        new[] { committed, inProgress, conflict }.Distinct().Count().ShouldBe(3);
    }
```

with:

```csharp
    public async Task The_409s_carry_distinct_machine_readable_codes()
    {
        // A client switches on §10.5's `code`, not on prose, so the codes are pinned.
        string committed = await CodeOfAsync(new CommandAlreadyCommittedException(Key));
        string inProgress = await CodeOfAsync(new ConcurrentRequestException(Guid.CreateVersion7()));
        string conflict = await CodeOfAsync(new DbUpdateConcurrencyException("stale"));
        string reused = await CodeOfAsync(new CommandIdReusedException(Guid.CreateVersion7()));

        committed.ShouldBe("command.already_committed");
        inProgress.ShouldBe("request.in_progress");
        conflict.ShouldBe("request.concurrency_conflict");
        reused.ShouldBe("command.id_reused");

        // Distinct as a set, since a code shared by two producers would satisfy each assertion alone.
        string[] codes = [committed, inProgress, conflict, reused];
        codes.Distinct().Count().ShouldBe(codes.Length);
    }
```

- [ ] **Step 4: Run and see them fail**

Run: `dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~CommandIdReusedExceptionHandlerTests|FullyQualifiedName~CommandAlreadyCommittedExceptionHandlerTests"`
Expected: `Failed!  - Failed: 7, Passed: 6`. The seven are
`The_409s_carry_distinct_machine_readable_codes` and every test of the new
class but `Any_other_exception_still_falls_through_to_the_500`, each on
`should be HttpStatusCode.Conflict but was HttpStatusCode.InternalServerError`.

- [ ] **Step 5: Write the handler**

Create `src/BuildingBlocks/Common.Web/CommandIdReusedExceptionHandler.cs`,
then `unix2dos` it:

```csharp
using Common.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Common.Web;

/// <summary>Translates <see cref="CommandIdReusedException"/> into §10.5's reused-identifier 409 row.</summary>
/// <remarks>The detail asks for a new identifier, because a retry meets the same refusal (ADR-057).</remarks>
internal sealed class CommandIdReusedExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not CommandIdReusedException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                // No CommandId in the body, because the key it forms carries the subject segment (§8.5).
                Detail =
                    "This command identifier was already used for a different request; " +
                    "send a changed request under a new identifier.",
                Extensions = { ["code"] = "command.id_reused" }
            }
        });

        return true;
    }
}
```

- [ ] **Step 6: Register it**

In `src/BuildingBlocks/Common.Web/ProblemDetailsExtensions.cs`, replace:

```csharp
        services.AddExceptionHandler<CommandAlreadyCommittedExceptionHandler>();
```

with:

```csharp
        services.AddExceptionHandler<CommandAlreadyCommittedExceptionHandler>();

        services.AddExceptionHandler<CommandIdReusedExceptionHandler>();
```

- [ ] **Step 7: Run the handler suites and see them pass**

Run: `dotnet test tests/Common.Web.Tests`
Expected: `Passed!  - Failed: 0, Passed: 213` at `b07cd0b2`.

- [ ] **Step 8: Run the wiring test and its control**

Run: `dotnet test tests/Ordering.Api.Tests --filter "FullyQualifiedName~PlaceOrderTests"`
Expected: `Passed!`, with `Failed: 0` —
`One_command_id_carrying_a_second_basket_is_refused_and_one_order_exists`
among them.

Run: `dotnet test tests/Catalog.Api.Tests --filter "FullyQualifiedName~ProductEndpointsTests.The_same_command_id_from_the_same_caller_replays_instead_of_publishing_twice"`
Expected: `Passed!  - Failed: 0, Passed: 1`. That test is not edited. It
sends one command twice through a real Redis, so it is what shows the new
envelope is written and read back over the real store, and that the 409 above
comes from the second basket and not from the second request.

- [ ] **Step 9: Commit, in two commits**

```bash
git branch --show-current
git add src/BuildingBlocks/Common.Web/CommandIdReusedExceptionHandler.cs \
    src/BuildingBlocks/Common.Web/ProblemDetailsExtensions.cs \
    tests/Common.Web.Tests/CommandIdReusedExceptionHandlerTests.cs \
    tests/Common.Web.Tests/CommandAlreadyCommittedExceptionHandlerTests.cs
git commit -F - <<'EOF'
feat(common): CommandIdReusedExceptionHandler answers 409 command.id_reused

Unregistered, the refusal IdempotencyBehavior now raises reaches
UseExceptionHandler as a 500, which a client retries into the same refusal
for as long as the entry lives. The handler answers 409 with a code a client
can switch on, and a detail that asks for a new identifier and does not say
retry: it is the second 409 here for which a retry is not the remedy. The
body carries no CommandId, because the key it forms carries the subject
segment.

The test that pins the 409 codes as a distinct set gains the fourth and
loses the count in its name, so the set has one owner and no number to keep.
EOF
git add tests/Ordering.Api.Tests/PlaceOrderTests.cs
git commit -F - <<'EOF'
test(ordering): PlaceOrderTests refuses a second basket under one CommandId

The one service test that proves the wiring: through the registered
pipeline, a real Redis and the host's exception handling, a second basket
under a CommandId already used is a 409 command.id_reused and one order
exists. Before the handler was registered this test answered 500, which is
what it is for. PlaceAsync takes an optional commandId so that a test can
pin one; every other caller still mints a fresh id.
EOF
```

---

### Task 4: ADR-057, its Appendix A row, §8.5 and §10.5

**Files:**
- Create: `docs/backend-architecture/adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md`
- Modify: `docs/backend-architecture/appendix-a-adrs.md:65` (the row after
  ADR-056's)
- Modify: `docs/backend-architecture/08-caching-redis.md:583`, `:618`,
  `:632`, `:686`, `:698-707` (the behaviour sample) and `:745-747` (the new
  callout, between the sample and *A claimed key belongs to one subject*)
- Modify: `docs/backend-architecture/10-api-gateway.md:824` (the registration
  sample), `:852` (the table), `:1068-1083` (two paragraphs)

**Interfaces:**
- Consumes: the names Tasks 2 and 3 produced, spelled exactly as there —
  `CommandFingerprint.Of`, `FingerprintPrefix`, `Envelope`,
  `Capture(result, fingerprint)`, `Replay(payload, fingerprint, commandId)`,
  `CommandIdReusedException`, `CommandIdReusedExceptionHandler` and
  `command.id_reused`.
- Produces: the ADR every comment in Tasks 2 and 3 cites, and the chapter
  text `/validate-blueprint` reads the code against.

The chapter edits are the paragraphs that state the rule and no others. A
sample is the code's second representation, so each sample line the code
changed changes too; a sentence that counts the 409s is edited where this PR
makes the count false. Nothing else in either chapter is opened: every other
mention of the mechanism now points at ADR-057 through §8.5's callout.

- [ ] **Step 1: Write the ADR**

Create
`docs/backend-architecture/adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md`
in `/new-adr`'s form — three bold-led paragraphs with no blank line between
them, and the footer and nothing after it:

```markdown
# ADR-057 — A command id is bound to the fingerprint of the command that claimed it

**Decision.** [§8.5](../08-caching-redis.md)'s `IdempotencyBehavior` stores a
successful command's result behind a fingerprint of the command that produced
it, and replays that result only to a command with the same fingerprint. The
fingerprint is `CommandFingerprint.Of`: SHA-256, in lower-case hex, over the
command serialised as the pipeline holds it, with default values omitted. It
is computed before the claim and written with the outcome, as
`sha256:{fingerprint}:{value}` in the payload
`IIdempotencyStore.CompleteAsync` already takes. On a key that is held, an
absent or unfinished entry is still `ConcurrentRequestException`; a completed
entry whose payload does not open with `sha256:` is replayed as it stands; one
whose fingerprint equals this command's, compared ordinally, is replayed; and
one whose fingerprint differs is refused with `CommandIdReusedException`,
which [§10.5](../10-api-gateway.md) answers 409 with `code`
`command.id_reused`. The handler does not run and the entry is left as it was.
**Why.** The key was bound to a subject and an operation, and to nothing the
request said. A client that sent a second, different request under a
`CommandId` it had already used was answered 200 with the first request's
result, and took its new request for applied: a success-shaped answer to work
that never ran. The command is hashed and not the HTTP body, because
[§4.2](../04-solution-structure.md) keeps HTTP out of `Common.Application`,
where the behaviour runs, and because a route value is part of what a request
asks for and no part of its body. Defaults are omitted so that a command may
gain an optional field without changing the fingerprint of a request that does
not send it. The fingerprint rides in the payload because the behaviour
already owns that string: the port, both Redis scripts and
[ADR-037](ADR-037-the-idempotency-marker-is-a-row-in-the-commands-own-transaction.md)'s
marker row are unchanged, and no service migrates.
**Consequences.** The shape of an idempotent command is a compatibility
surface, as §8.5 already says of its result: removing or renaming a field
changes the fingerprint of requests already answered, so a retry that
straddles that deploy is refused as reused. The fingerprint is of the bound
command, so two bodies that bind to equal commands match and two that bind
differently do not: a reordered list of lines is a different request, and so
is an amount sent as `10.0` where the first attempt sent `10`. A retry
re-sends the bytes it sent. An in-flight duplicate with different content is
told `request.in_progress` and not `command.id_reused`, because the
fingerprint is recorded with the outcome and there is nothing to compare until
the first attempt completes; recording it at the claim would change the port,
both scripts and every implementer of the port, to move one answer one retry
earlier. The marker row carries no fingerprint: once the claim has expired,
any reuse of the key is refused with `command.already_committed` whatever it
carries, which is already a refusal. A refused command still stores nothing,
so the same id may carry a corrected request. An entry written before this
record carries no fingerprint and replays to any command for what is left of
its claim's window. During a rolling deploy, a replica on the previous release
that meets a new payload fails the replay of a command that returns a value
with a 500, because it deserialises the prefix, and replays a command that
returns none as it always did, uncompared; nothing is applied twice, and the
retry is answered by this record's rule once the rollout completes. A command
the serialiser refuses now fails before its claim, where it used to run. The
payload is no longer a JSON document, and it still cannot spell the store's
in-progress state.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
```

The spec's *What this does not do* and *Residuals* are the *Consequences*
paragraph, its rolling-deploy residual included: a replica on the
previous release fails the replay of a command that returns a value, and
replays one that returns none as it always did, because the previous
`Replay` returns before reading a void payload.

- [ ] **Step 2: Add the index row**

In `docs/backend-architecture/appendix-a-adrs.md`, replace:

```markdown
| **ADR-056** | [A service's fixture derives from one shared body under tests/](adr/ADR-056-a-services-fixture-derives-from-one-shared-body-under-tests.md) |
```

with:

```markdown
| **ADR-056** | [A service's fixture derives from one shared body under tests/](adr/ADR-056-a-services-fixture-derives-from-one-shared-body-under-tests.md) |
| **ADR-057** | [A command id is bound to the fingerprint of the command that claimed it](adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md) |
```

Nothing else in Appendix A changes.

- [ ] **Step 3: Amend §8.5's behaviour sample**

Five edits to the `IdempotencyBehavior` sample in
`docs/backend-architecture/08-caching-redis.md`, so that it and the source
read alike.

Edit 1 — the prefix. Replace:

```csharp
    // ConcurrentRequestException for a day. This is valid JSON and unambiguous.
    private const string NoValue = "null";
```

with:

```csharp
    // ConcurrentRequestException for a day. This is valid JSON and unambiguous.
    private const string NoValue = "null";

    // What a payload opens with when it carries the fingerprint of the command
    // that produced it (ADR-057). No JSON value begins with "s", so no bare
    // value a previous release stored can spell it.
    private const string FingerprintPrefix = "sha256:";
```

Edit 2 — the fingerprint, before the claim. Replace:

```csharp
        string key = $"{Subject()}:{TCommand.OperationName}:{command.CommandId}";

        // The token names THIS attempt, and every write below carries it.
```

with:

```csharp
        string key = $"{Subject()}:{TCommand.OperationName}:{command.CommandId}";

        // Before the claim and not beside the replay: a command the serialiser
        // refuses then throws while it holds no key, where a throw after
        // TryClaimAsync would leave one held for the whole retention.
        string fingerprint = CommandFingerprint.Of(command);

        // The token names THIS attempt, and every write below carries it.
```

Edit 3 — the replay call. Replace:

```csharp
            return Replay(existing.Payload!);
```

with:

```csharp
            return Replay(existing.Payload!, fingerprint, command.CommandId);
```

Edit 4 — the completion call. Replace:

```csharp
        await store.CompleteAsync(key, claim, Capture(result), CancellationToken.None);
```

with:

```csharp
        await store.CompleteAsync(key, claim, Capture(result, fingerprint), CancellationToken.None);
```

Edit 5 — `Capture`, `Envelope` and the head of `Replay`. Replace:

```csharp
    // measured in "Trap — JSON round-tripping the Result itself".
    private static string Capture(TResult result) =>
        ValueType is null
            ? NoValue
            : JsonSerializer.Serialize(ValueProperty!.GetValue(result), ValueType);

    private static TResult Replay(string payload)
    {
        // (TResult)Result.Success() is legal C# under the constraint above and
```

with:

```csharp
    // measured in "Trap — JSON round-tripping the Result itself". The value
    // goes behind the fingerprint of the command that produced it (ADR-057).
    private static string Capture(TResult result, string fingerprint)
    {
        string value = ValueType is null
            ? NoValue
            : JsonSerializer.Serialize(ValueProperty!.GetValue(result), ValueType);

        return Envelope(fingerprint) + value;
    }

    private static string Envelope(string fingerprint) => $"{FingerprintPrefix}{fingerprint}:";

    private static TResult Replay(string payload, string fingerprint, Guid commandId)
    {
        // Compared before anything is read, the void shape included: the guard
        // below never looks at the payload. An entry a previous release wrote
        // opens with no prefix and replays as it stands (ADR-057). The envelope
        // is stripped by its length, so a ":" inside the value is no separator.
        if (payload.StartsWith(FingerprintPrefix, StringComparison.Ordinal))
        {
            string envelope = Envelope(fingerprint);

            if (!payload.StartsWith(envelope, StringComparison.Ordinal))
                throw new CommandIdReusedException(commandId);

            payload = payload[envelope.Length..];
        }

        // (TResult)Result.Success() is legal C# under the constraint above and
```

The comment that closes both blocks is the sample's own and is quoted only to
place the edit; it and everything below it stay as they are.

- [ ] **Step 4: State the rule in §8.5**

In the same file, the callout goes between the sample's closing fence and the
callout on the subject. Replace:

````markdown
}
```

> **A claimed key belongs to one subject, and that is the invariant rather than
````

with:

````markdown
}
```

> **Decision — a key is bound to the command that claimed it.** See
> [ADR-057](adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md).
> A completed entry is replayed to the command that produced it and to no
> other. `Capture` stores the value behind `CommandFingerprint.Of(command)` —
> a SHA-256 of the command as the pipeline holds it, defaults omitted — and
> `Replay` compares before it reads. A different command under the same key is
> refused with `CommandIdReusedException`, [§10.5](10-api-gateway.md)'s
> `command.id_reused`, and is neither replayed nor run: a 200 carrying the
> first request's result would tell the caller its second request was applied.
> An in-flight duplicate is still `ConcurrentRequestException` whatever it
> carries, because the fingerprint is recorded with the outcome.
>
> **The shape of an idempotent command is therefore a compatibility surface,
> on the terms the callout on renaming sets for its result.** Removing or
> renaming a field changes the fingerprint of requests already answered, so a
> retry that straddles that deploy is refused as reused; adding an optional
> field does not, because a default is not hashed.

> **A claimed key belongs to one subject, and that is the invariant rather than
````

*The callout on renaming* is the existing one that begins **Renaming a
command would change its keys**, whose second half says a result's shape is a
migration. §8.5's opening paragraph is not touched: it is ADR-058's.

- [ ] **Step 5: Amend §10.5**

Five edits to `docs/backend-architecture/10-api-gateway.md`.

Edit 1 — the registration sample. Replace:

```csharp
    services.AddExceptionHandler<CommandAlreadyCommittedExceptionHandler>();

    return services.AddProblemDetails(options =>
```

with:

```csharp
    services.AddExceptionHandler<CommandAlreadyCommittedExceptionHandler>();
    services.AddExceptionHandler<CommandIdReusedExceptionHandler>();

    return services.AddProblemDetails(options =>
```

Edit 2 — the count inside the `command.already_committed` row, which this PR
makes false. In the table row that begins
`| The command under this key has already been applied | 409 |`, replace:

```markdown
`code` `command.already_committed`. The one 409 here that does **not** say retry, which is why
```

with:

```markdown
`code` `command.already_committed`. A 409 that does **not** say retry, which is why
```

Edit 3 — the new row, directly under that one. The row above ends with the
text quoted first; replace:

```markdown
a success-shaped answer to a request whose result this service cannot produce |
```

with:

```markdown
a success-shaped answer to a request whose result this service cannot produce |
| The command identifier was already used for a different request | 409 | From `CommandIdReusedException` ([§8.5](08-caching-redis.md), [ADR-057](adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md)), `code` `command.id_reused`. It does **not** say retry either: the key's entry holds another command's result, so the same request under the same identifier meets this refusal for as long as that entry lives. A changed request is a new request and takes a new identifier. 200 with the stored result is what this row replaced — a success-shaped answer to a request that was never applied |
```

Edit 4 — the paragraph that names the codes, which begins **Every 409
carries a `code`**. Replace its last four lines:

```markdown
for exactly this: `request.concurrency_conflict`, `request.in_progress` and
`command.already_committed`. The `Error` path has carried a `code` since
PR-18; the exception path carried none until a contradiction made the absence
cost something.
```

with:

```markdown
for exactly this: `request.concurrency_conflict`, `request.in_progress` and
`command.already_committed`, and the producer
[ADR-057](adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md)
added names itself `command.id_reused` on the same terms. The `Error` path has
carried a `code` since PR-18; the exception path carried none until a
contradiction made the absence cost something.
```

Edit 5 — the paragraph after it, whose opening counts the 409s §8.5
produces. Replace:

```markdown
**The two 409s from §8.5 are still told apart by `detail` for a human, and
that is the design rather than a shortage of statuses.** They share the statement — this
request conflicts with work already in hand — and differ in what the client
should do about it, which is prose a client reads and not a code it switches
on. Inventing a status for the second would be inventing one for a distinction
HTTP does not draw.
```

with:

```markdown
**The 409s from §8.5 are still told apart by `detail` for a human, and that
is the design rather than a shortage of statuses.** They share the statement —
this request conflicts with work already in hand — and differ in what the
client should do about it, which is prose a client reads and not a code it
switches on. Inventing a status for one of them would be inventing one for a
distinction HTTP does not draw.
```

The paragraphs above these that give `CommandAlreadyCommittedExceptionHandler`
its ordinals describe the order the handlers arrived in, stay true, and are
not edited.

- [ ] **Step 6: Check the links and the wrap**

Run: `/check-links`
Expected: no finding against a line this task added — the ADR's links and its
footer resolve, both chapters' links to the ADR resolve, and check 5 finds
one ADR-057. The command takes no argument and reads the whole blueprint; a
finding elsewhere is not this PR's.

Run: `awk 'length > 80' docs/backend-architecture/adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md`
Expected: two lines — the heading and the bare ADR-037 link.

Run: `git diff --unified=0 -- docs/backend-architecture/08-caching-redis.md docs/backend-architecture/10-api-gateway.md | grep '^+' | grep -v '^+++' | awk 'length > 81'`
Expected: seven lines — three lines of the sample's code, the two table rows
and the two bare ADR links. Every line of prose this task added wraps at 80.

- [ ] **Step 7: Commit**

```bash
git branch --show-current
git add docs/backend-architecture/adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md \
    docs/backend-architecture/appendix-a-adrs.md \
    docs/backend-architecture/08-caching-redis.md \
    docs/backend-architecture/10-api-gateway.md
git commit -F - <<'EOF'
docs(adr): ADR-057 binds a command id to its command's fingerprint, in §8.5 and §10.5

The rule the code now keeps had no owner in the blueprint. ADR-057 records
it with what it costs: the shape of an idempotent command is a compatibility
surface, the fingerprint is of the bound command and not of the body, an
in-flight duplicate with different content is still told to retry, and a
replica on the previous release cannot read the new payload of a command
that returns a value.

§8.5's sample moves with the source it mirrors and gains the callout that
states the rule; §10.5 gains the registration line and the table row,
because that table owns the status mapping. Two sentences in §10.5 that
counted the 409s lose their counts, since this change made them false.
Nothing else in either chapter is opened: the ADR is the correction, and
§8.5's opening rule is left for the record that amends it.
EOF
```

---

### Task 5: Verification and the PR

**Files:** none.

**Interfaces:**
- Consumes: the four commits of Tasks 2 to 4.
- Produces: an open pull request whose body carries the class and the
  touch-set row.

- [ ] **Step 1: Build**

Run: `dotnet build Platform.slnx`
Expected: `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 2: The fast half, solution-wide**

Run: `dotnet test Platform.slnx --filter "Category!=Integration"`
Expected: every test project reports `Passed!` with `Failed: 0`.

- [ ] **Step 3: The container suites that can fail for this change**

Run: `docker info --format '{{.ServerVersion}}'`
Expected: a version number.

Run: `dotnet test tests/Ordering.Api.Tests --filter "Category=Integration"`
Expected: `Passed!`, `Failed: 0` — the wiring test and Ordering's
`IdempotencyMarkerTests` among them.

Run: `dotnet test tests/Catalog.Api.Tests --filter "Category=Integration"`
Expected: `Passed!`, `Failed: 0` — the HTTP replay over a real Redis among
them.

Run: `dotnet test tests/Common.Infrastructure.Tests --filter "Category=Integration"`
Expected: `Passed!`, `Failed: 0` — `RedisIdempotencyStoreTests`, which this
PR does not edit and must not break.

- [ ] **Step 4: Format and line endings**

Run: `dotnet format src/BuildingBlocks/Common.Application/Common.Application.csproj --verify-no-changes --no-restore`
Run: `dotnet format src/BuildingBlocks/Common.Web/Common.Web.csproj --verify-no-changes --no-restore`
Run: `dotnet format tests/Common.Application.Tests/Common.Application.Tests.csproj --verify-no-changes --no-restore`
Run: `dotnet format tests/Common.Web.Tests/Common.Web.Tests.csproj --verify-no-changes --no-restore`
Run: `dotnet format tests/Ordering.Api.Tests/Ordering.Api.Tests.csproj --verify-no-changes --no-restore`
Expected: each exits 0 and prints nothing.

Run: `git ls-files --eol src/BuildingBlocks/Common.Application/CommandFingerprint.cs src/BuildingBlocks/Common.Application/CommandIdReusedException.cs src/BuildingBlocks/Common.Web/CommandIdReusedExceptionHandler.cs tests/Common.Application.Tests/CommandFingerprintTests.cs tests/Common.Web.Tests/CommandIdReusedExceptionHandlerTests.cs`
Expected: every line begins `i/lf    w/crlf  attr/text eol=crlf`.

- [ ] **Step 5: The gates this diff can trip**

Run: `git fetch origin main`
Run: `py -3.12 .github/comment-gate/comment_gate.py --base origin/main`
Expected: `0 finding(s)`, and a C# line on which comment lines are far fewer
than code lines.

Run: `py -3.12 .github/secret-scan/secret_scan.py`
Expected: `0 unexplained`. No test in this plan prints a digest as a
literal; each computes it.

Run, from `.github/licence-gate`: `py -3.12 licence_gate.py`
Expected: exit 0 — no chapter this PR edits prints a pin.

Run: `git diff --name-only origin/main...HEAD`
Expected: exactly the fourteen paths of *File Structure*, and no `.csproj`.

- [ ] **Step 6: The blueprint audit, last**

Dispatch a subagent to run
`/validate-blueprint docs/backend-architecture/08-caching-redis.md` and then
`/validate-blueprint docs/backend-architecture/10-api-gateway.md`, and to
report findings without editing.
Expected: no finding in the paragraphs and sample lines Task 4 wrote, and no
type-or-member drift between §8.5's sample and `IdempotencyBehavior.cs`. A
finding elsewhere in either chapter is not this PR's: name it in the PR body
and leave it.

- [ ] **Step 7: Open the pull request**

Title:
`feat(common): a command id is bound to the fingerprint of the command that claimed it (ADR-057)`

The body's metadata table opens with these two rows, the second exactly as
printed:

```markdown
| | |
|---|---|
| Class | C |
| Touch set | `src/BuildingBlocks/Common.Application/**`, `src/BuildingBlocks/Common.Web/**`, `tests/Common.Application.Tests/**`, `tests/Common.Web.Tests/**`, `tests/Ordering.Api.Tests/PlaceOrderTests.cs`, `docs/backend-architecture/adr/ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md`, `docs/backend-architecture/appendix-a-adrs.md`, `docs/backend-architecture/08-caching-redis.md`, `docs/backend-architecture/10-api-gateway.md` |
```

The reasons for each path go under the table, as Global Constraints gives
them, never inside the cell. The body then carries, as its honest cost:

- the residuals ADR-057's *Consequences* state, in one sentence each;
- that `blueprint-frontend`'s `error-mapper.ts` does not yet know
  `command.id_reused`, and that its fallback for an unread 409 already
  forbids a retry, so the client is safe and merely less exact until its own
  PR lands;
- that `RedisIdempotencyStore`'s comment on its in-progress marker still
  reasons from payloads being JSON; the marker stays unspellable, because a
  payload now opens with `sha256:`, and the file is deliberately not in this
  touch set;
- the build and test results of Steps 1 to 3, as they came out.

Run: `/pr feat(common): a command id is bound to the fingerprint of the command that claimed it (ADR-057)`
Expected: a PR URL. The locality gate, the closure gate and the comment gate
then run on it; a red locality check means a path outside the row, and the
answer is a narrower diff or a row widened with its reason beside it, never a
row widened silently.

- [ ] **Step 8: Record the PR**

`TODO.md` at the main checkout's root gains the open PR. From inside a
worktree that file is out of reach, so carry the update in the final report
as owed and make it once the session is back in the main checkout.
