using System.Reflection;
using Payments.Application;
using Payments.Infrastructure.Persistence;
using Payments.TestSupport;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §8.5's durable marker against a real engine. The property is atomicity
/// with the command's own transaction, and nothing short of a database can
/// show it: a fake unit of work commits nothing, so "the marker rolled back
/// with the work" and "the marker was never written" look the same from
/// every assertion a double can make. These tests require Docker and are
/// deliberately not skipped without it, on <c>DatabaseSmokeTests</c>'
/// terms — <see cref="IntegrationCollection"/> carries the category.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public class IdempotencyMarkerTests(ServiceFixture fixture)
{
    [Fact]
    public async Task A_committed_command_leaves_a_marker_under_its_key()
    {
        string key = Key();
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Result result = await RunAsync(scope, key, id, Result.Success());

        result.IsSuccess.ShouldBeTrue();
        (await fixture.ProbeRowCountAsync(id)).ShouldBe(1);
        (await fixture.IdempotencyMarkerCountAsync(key)).ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_command_leaves_neither_its_work_nor_its_marker()
    {
        // Both halves in one assertion, because either alone would pass against
        // a marker written on its own connection: the row count proves the
        // transaction rolled back, and the marker count proves the marker was
        // inside it. A marker that survived a rollback would refuse every later
        // attempt at a command that never committed — a permanent refusal
        // nothing in the system could explain.
        string key = Key();
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Result result = await RunAsync(
            scope,
            key,
            id,
            Result.Failure(Error.Rule("probe.rejected", "The handler rejected the command.")));

        result.IsFailure.ShouldBeTrue();
        (await fixture.ProbeRowCountAsync(id)).ShouldBe(0);
        (await fixture.IdempotencyMarkerCountAsync(key)).ShouldBe(0);
    }

    [Fact]
    public async Task A_second_attempt_under_a_committed_key_is_refused_and_writes_nothing()
    {
        // The defect this closes, end to end. The first attempt commits; §8.5's
        // Redis claim is then released by the lost acknowledgement it cannot
        // detect, so the second attempt arrives holding a fresh claim over work
        // that is already durable. Before the marker it wrote the row twice.
        string key = Key();
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();

        await using (AsyncServiceScope one = fixture.Factory.Services.CreateAsyncScope())
            await RunAsync(one, key, first, Result.Success());

        await using AsyncServiceScope two = fixture.Factory.Services.CreateAsyncScope();

        CommandAlreadyCommittedException thrown =
            await Should.ThrowAsync<CommandAlreadyCommittedException>(
                () => RunAsync(two, key, second, Result.Success()));

        thrown.Key.ShouldBe(key);
        (await fixture.ProbeRowCountAsync(second)).ShouldBe(
            0,
            "the handler never ran, so the second attempt's work was never done");
        (await fixture.IdempotencyMarkerCountAsync(key)).ShouldBe(
            1,
            "and the refusal left the first attempt's marker exactly as it found it");
    }

    [Fact]
    public async Task A_committed_marker_is_stamped_by_the_database_and_not_left_at_its_sentinel()
    {
        // The one property nothing else in this suite can see. CommittedAt
        // is a store default (ADR-038): MarkAsync constructs the row without
        // a timestamp, EF omits a property still holding its sentinel from
        // the INSERT, and SYSDATETIMEOFFSET() supplies the column. Every
        // other test stages its markers with an explicit timestamp instead,
        // so all of them stay green if EF ever sends the sentinel. The
        // assertion is the sentinel rather than a value: nothing here can
        // prove which clock wrote a plausible timestamp, only that the
        // column was not left at 0001-01-01, the state that makes every
        // marker purgeable the moment it is written, §8.5's guarantee retired.
        string key = Key();
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Result result = await RunAsync(scope, key, id, Result.Success());

        result.IsSuccess.ShouldBeTrue();

        // Keyed rather than read whole, for the reason ServiceFixture's
        // InboxAsync(Guid) gives one table over: classes in this collection
        // share the fixture and run in sequence, so an unkeyed read asserts
        // test isolation alongside the claim and fails on the half that is
        // nobody's.
        IdempotencyMarker marker = (await fixture.IdempotencyMarkersAsync())
            .Where(candidate => candidate.Key == key)
            .ShouldHaveSingleItem();

        marker.CommittedAt.ShouldNotBe(
            default,
            "the marker was written at the CLR sentinel of 0001-01-01, so the store default never " +
            "fired and this row is already older than any retention window it could be given");

        // An hour either side, and generous on purpose. SYSDATETIMEOFFSET()
        // reads the SQL Server container's clock, which is this host's, so the
        // two agree far closer than that — the width is here so the test
        // cannot fail for a reason that is not its subject, and it still
        // refuses the sentinel by two thousand years.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        marker.CommittedAt.ShouldBeInRange(
            now.AddHours(-1),
            now.AddHours(1),
            "a stamp this far from now is not a clock at all");
    }

    [Fact]
    public async Task The_stamp_above_comes_from_a_default_constraint_on_the_column()
    {
        // The mechanism the test above is looking at, asserted separately
        // because the two fail apart: a marker could carry a plausible
        // timestamp because some insert path wrote one, and from the value
        // alone that is indistinguishable from the default firing. This is
        // what makes the stamp a property of the schema rather than of one
        // caller — and it is the half that fails if the migration is ever
        // regenerated without the default.
        //
        // The form is OrderFulfilmentSagaEndpointTests' pair over the saga's
        // retained column, which is the same claim about a different default.
        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id = d.parent_object_id
                AND c.column_id = d.parent_column_id
            WHERE d.parent_object_id = OBJECT_ID('payments.IdempotencyMarkers')
                AND c.name = 'CommittedAt'
            """))
            .ShouldBe(
                1,
                "ADR-038 puts the marker's age on the database's clock, and the constraint is the " +
                "only thing that stamps a row whose INSERT omits the column");

        // Lowered, because SQL Server keeps a constraint's definition as the
        // text it was written with — a hand-written migration may spell the
        // function in any case, and the case is no part of what this asserts.
        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = LOWER(d.definition)
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id = d.parent_object_id
                AND c.column_id = d.parent_column_id
            WHERE d.parent_object_id = OBJECT_ID('payments.IdempotencyMarkers')
                AND c.name = 'CommittedAt'
            """))
            .ShouldContain(
                "sysdatetimeoffset",
                customMessage: "the count above is satisfied by any default at all — GETUTCDATE(), " +
                "or a literal, which is not a clock in any sense. The cutoff is DATEADD over " +
                "SYSDATETIMEOFFSET(), so the two sides are only guaranteed comparable when the " +
                "column is written by the same expression; this asserts the contract rather than " +
                "merely that some default exists");
    }

    [Fact]
    public async Task The_row_the_purge_deletes_is_identified_by_a_database_generated_rowversion()
    {
        // The subject is what the delete is looking at, not what a purge
        // pass found — RetentionPurgeTests covers the behaviour, and it
        // would go on passing if this column quietly stopped being a
        // rowversion, since a plain binary(8) nobody updates still differs
        // between two rows in one test. What must be true is that the
        // database generates it (ADR-041): a value the application could
        // write is a value a replacement could carry. Read from the model
        // and sys.columns rather than restated, so a claim written here and
        // one written in the configuration agree until one is edited.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        IProperty version = db.Model
            .FindEntityType(typeof(IdempotencyMarker))!
            .FindProperty(IdempotencyMarker.RowVersionColumn)
            .ShouldNotBeNull(
                "RetentionPurgeService selects and joins on this column by name, so a service " +
                "that maps it under another name — or not at all — fails its own purge at run " +
                "time with an invalid column name");

        version.IsShadowProperty().ShouldBeTrue(
            "no CLR property backs it on purpose: the one reader is the purge's SQL, over Dapper");

        version.ValueGenerated.ShouldBe(
            ValueGenerated.OnAddOrUpdate,
            "a version the application supplies is a version a replacement could be given, which " +
            "breaks the identity-by-construction ADR-041 requires");

        version.IsConcurrencyToken.ShouldBeTrue("IsRowVersion() is what sets both, and both matter");

        version.IsNullable.ShouldBeFalse(
            "SQL Server stamps every row including the ones ALTER TABLE adds the column to, so a " +
            "nullable mapping models a state the database cannot produce");

        // rowversion and timestamp are one type under two spellings, and
        // sys.columns reports the older one — so this asserts the engine's
        // answer rather than the migration's text.
        (await fixture.ScalarAsync<string>(
            $"""
            SELECT Value = TYPE_NAME(c.system_type_id)
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID('payments.IdempotencyMarkers')
                AND c.name = '{IdempotencyMarker.RowVersionColumn}'
            """))
            .ShouldBe(
                "timestamp",
                "a binary(8) with the same name would satisfy every model assertion above and " +
                "leave the column something an INSERT can choose");
    }

    [Fact]
    public async Task Every_operation_name_leaves_room_for_the_key_it_forms()
    {
        // §8.5's key is {subject}:{operation}:{commandId}, and the marker column
        // is what it has to fit in. A name too long for it does not fail at
        // build time or at startup: SQL Server refuses the insert on the first
        // dispatch of that command, and the transaction it refuses is the one
        // carrying the customer's order.
        //
        // The width is read from the model rather than restated here, because a
        // 450 written in a test and a 450 written in a configuration agree
        // until one of them is edited.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        int width = db.Model
            .FindEntityType(typeof(IdempotencyMarker))!
            .FindProperty(nameof(IdempotencyMarker.Key))!
            .GetMaxLength()!
            .Value;

        // A GUID rendered "D" for the subject, another for the command, and the
        // two separators. The subject may also be the literal "system", which is
        // shorter — so the GUID is the case to budget for.
        int spent = (Guid.Empty.ToString().Length * 2) + 2;

        string[] offenders =
        [
            .. Operations()
                .Where(operation => operation.Length > width - spent)
                .Select(operation => $"{operation} ({operation.Length} characters)")
        ];

        offenders.ShouldBeEmpty(
            $"the marker key is {spent} characters of GUIDs and separators plus the operation " +
            $"name, and the column holds {width}");
    }

    [Fact]
    public async Task This_service_has_no_operation_names_for_the_gate_above_yet()
    {
        // ShouldBeEmpty is green when the selection found nothing, which is the
        // one reason a gate must never pass. Asserting the subject separately
        // is the only thing that tells the two apart.
        await Task.CompletedTask;

        Operations().ShouldBeEmpty(
            "This service opts no command into idempotency yet, so the width gate above is " +
            "vacuous. The day it does, this test fails — replace it with the ShouldNotBeEmpty " +
            "form, which is what keeps a vacuous gate from quietly becoming a permanent one.");
    }

    private static string[] Operations() =>
        [
            .. typeof(Payments.Application.DependencyInjection).Assembly
                .GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false })
                .Where(typeof(IIdempotentCommand).IsAssignableFrom)
                .Select(t => (string)t
                    .GetProperty(
                        nameof(IIdempotentCommand.OperationName),
                        BindingFlags.Public | BindingFlags.Static)!
                    .GetValue(null)!)
        ];

    private static string Key() =>
        $"{Guid.CreateVersion7()}:tests.marker:{Guid.CreateVersion7()}";

    /// <summary>
    /// One command through the real §6.3 behaviour over the scope's real unit
    /// of work, with the key already on the context — which is what §8.5's
    /// behaviour puts there after a successful claim. The handler writes
    /// through <c>ExecuteRawAsync</c>, so the work and the marker are on the
    /// same connection and either both commit or neither does.
    /// </summary>
    private static Task<Result> RunAsync(AsyncServiceScope scope, string key, Guid id, Result outcome)
    {
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        scope.ServiceProvider.GetRequiredService<IdempotencyContext>().Claim(key);

        TransactionBehavior<ProbeCommand, Result> behaviour = new(
            unitOfWork,
            scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>(),
            scope.ServiceProvider.GetRequiredService<IIdempotencyMarkerStore>(),
            scope.ServiceProvider.GetRequiredService<IdempotencyContext>());

        CancellationToken ct = TestContext.Current.CancellationToken;

        return behaviour.HandleAsync(
            new ProbeCommand(),
            async () =>
            {
                await unitOfWork.ExecuteRawAsync(
                    "INSERT INTO payments.TransactionProbe (Id, Note) VALUES (@Id, @Note)",
                    new { Id = id, Note = "written beside a marker" },
                    ct);

                return outcome;
            },
            ct);
    }
}
