using System.Reflection;
using Shipping.Application;
using Shipping.Infrastructure.Persistence;
using Shipping.TestSupport;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>§8.5's durable marker against a real engine, the one thing that shows it shares the work's fate.</summary>
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
        // Both halves, since either alone would pass against a marker written on its own connection.
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
        // §8.5's claim can be released after a commit it cannot see, so the second attempt holds a fresh claim.
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
        // CommittedAt is a store default (ADR-038). The sentinel, not a value, is asserted: at 0001-01-01 a
        // marker is purgeable the moment it is written.
        string key = Key();
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Result result = await RunAsync(scope, key, id, Result.Success());

        result.IsSuccess.ShouldBeTrue();

        // Keyed, since the collection's classes share this fixture in sequence.
        IdempotencyMarker marker = (await fixture.IdempotencyMarkersAsync())
            .Where(candidate => candidate.Key == key)
            .ShouldHaveSingleItem();

        marker.CommittedAt.ShouldNotBe(
            default,
            "the marker was written at the CLR sentinel of 0001-01-01, so the store default never " +
            "fired and this row is already older than any retention window it could be given");

        // An hour either side, generous on purpose, and still two thousand years from the sentinel.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        marker.CommittedAt.ShouldBeInRange(
            now.AddHours(-1),
            now.AddHours(1),
            "a stamp this far from now is not a clock at all");
    }

    [Fact]
    public async Task The_stamp_above_comes_from_a_default_constraint_on_the_column()
    {
        // The mechanism, asserted apart because a plausible timestamp could come from an insert path rather than
        // the default; this is the half that fails if the migration is regenerated without it.
        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id = d.parent_object_id
                AND c.column_id = d.parent_column_id
            WHERE d.parent_object_id = OBJECT_ID('shipping.IdempotencyMarkers')
                AND c.name = 'CommittedAt'
            """))
            .ShouldBe(
                1,
                "ADR-038 puts the marker's age on the database's clock, and the constraint is the " +
                "only thing that stamps a row whose INSERT omits the column");

        // Lowered, since SQL Server keeps a constraint's definition in the case it was written in.
        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = LOWER(d.definition)
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id = d.parent_object_id
                AND c.column_id = d.parent_column_id
            WHERE d.parent_object_id = OBJECT_ID('shipping.IdempotencyMarkers')
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
        // What the delete is looking at: the database generates the version (ADR-041), since a value the
        // application could write is one a replacement could carry.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

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
            "breaks the identity-by-construction this column exists to give the row");

        version.IsConcurrencyToken.ShouldBeTrue("IsRowVersion() is what sets both, and both matter");

        version.IsNullable.ShouldBeFalse(
            "SQL Server stamps every row including the ones ALTER TABLE adds the column to, so a " +
            "nullable mapping models a state the database cannot produce");

        // rowversion and timestamp are one type, and sys.columns reports the older spelling.
        (await fixture.ScalarAsync<string>(
            $"""
            SELECT Value = TYPE_NAME(c.system_type_id)
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID('shipping.IdempotencyMarkers')
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
        // A name too long for §8.5's key fails no build or startup, only the insert on its first dispatch.
        // The width is read from the model rather than restated.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        int width = db.Model
            .FindEntityType(typeof(IdempotencyMarker))!
            .FindProperty(nameof(IdempotencyMarker.Key))!
            .GetMaxLength()!
            .Value;

        // Two GUIDs rendered "D" and two separators; the subject "system" is shorter, so the GUID is budgeted.
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
        // The gate's subject, asserted apart, since ShouldBeEmpty is green on an empty selection.
        await Task.CompletedTask;

        Operations().ShouldBeEmpty(
            "This service opts no command into idempotency yet, so the width gate above is " +
            "vacuous. The day it does, this test fails — replace it with the ShouldNotBeEmpty " +
            "form, which is what keeps a vacuous gate from quietly becoming a permanent one.");
    }

    private static string[] Operations() =>
        [
            .. typeof(Shipping.Application.DependencyInjection).Assembly
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

    /// <summary>One command through §6.3's real behaviour, with the key already on the context (§8.5).</summary>
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
                    "INSERT INTO shipping.TransactionProbe (Id, Note) VALUES (@Id, @Note)",
                    new { Id = id, Note = "written beside a marker" },
                    ct);

                return outcome;
            },
            ct);
    }
}
