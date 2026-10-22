using System.Text.Json;
using Common.Application;
using Common.Contracts.Privacy.V1;
using Common.Infrastructure.Outbox;
using Common.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Privacy.Infrastructure.Persistence;
using Privacy.Migrator;
using Privacy.TestSupport;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary><see cref="PrivacyRepublisher"/> through the real job host, on databases the collection never
/// reads.</summary>
[Collection(nameof(IntegrationCollection))]
public class PrivacyRepublisherTests(ServiceFixture fixture)
{
    private static readonly DateTimeOffset Raised = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Answered = new(2026, 3, 2, 10, 15, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Amended = new(2026, 3, 3, 11, 45, 0, TimeSpan.Zero);

    private static readonly Guid ClosedByAnswer = Guid.Parse("b0000000-0000-0000-0000-000000000001");

    private static readonly Guid ClosedByAmendment = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    private static readonly Guid ClosedOverSilence = Guid.Parse("b0000000-0000-0000-0000-000000000003");

    private static readonly Guid StillOpen = Guid.Parse("b0000000-0000-0000-0000-000000000004");

    private static readonly Guid Overdue = Guid.Parse("b0000000-0000-0000-0000-000000000005");

    [Fact]
    public async Task Each_close_that_may_be_announced_is_staged_with_the_close_as_its_stamp()
    {
        string database = await RequestsAsync();

        DateTimeOffset before = DateTimeOffset.UtcNow;
        (await RunAsync(database)).ShouldBe(0);

        IReadOnlyList<(OutboxMessage Row, PersonalDataDeleteClosed Event)> rows = await StagedAsync(database);

        rows.Select(r => r.Event.RequestId)
            .Order()
            .ToArray()
            .ShouldBe(
                new[] { ClosedByAnswer, ClosedByAmendment },
                "a close over a removed holder never heard from, and every unclosed request, are left out");

        rows.Single(r => r.Event.RequestId == ClosedByAnswer).Event.OccurredAt.ShouldBe(Answered);
        rows.Single(r => r.Event.RequestId == ClosedByAmendment).Event.OccurredAt.ShouldBe(Amended);

        // The row is the run's own, so §13.7's age measures this backlog and the dispatcher's order is the staging's.
        foreach ((OutboxMessage row, PersonalDataDeleteClosed @event) in rows)
        {
            row.Lane.ShouldBe(OutboxLane.Broker);
            row.Attempts.ShouldBe(0);
            row.ProcessedAt.ShouldBeNull();
            row.OccurredAt.ShouldBeGreaterThanOrEqualTo(before);
            @event.MessageId.ShouldBe(row.MessageId);
            @event.CorrelationId.ShouldBe(@event.RequestId);
            row.CorrelationId.ShouldBe(@event.RequestId);
        }
    }

    [Fact]
    public async Task A_second_run_stages_the_closes_again_under_new_message_ids()
    {
        string database = await RequestsAsync();

        (await RunAsync(database)).ShouldBe(0);
        (await RunAsync(database)).ShouldBe(0);

        IReadOnlyList<(OutboxMessage Row, PersonalDataDeleteClosed Event)> rows = await StagedAsync(database);

        rows.Count.ShouldBe(4);
        rows.Select(r => r.Row.MessageId).Distinct().Count().ShouldBe(4);
    }

    [Fact]
    public async Task One_request_named_stages_its_own_close_and_no_other()
    {
        string database = await RequestsAsync();

        (await RunAsync(database, ClosedByAmendment)).ShouldBe(0);

        IReadOnlyList<(OutboxMessage Row, PersonalDataDeleteClosed Event)> rows = await StagedAsync(database);

        rows.ShouldHaveSingleItem().Event.RequestId.ShouldBe(ClosedByAmendment);
    }

    [Theory]
    [InlineData("b0000000-0000-0000-0000-000000000003")]
    [InlineData("b0000000-0000-0000-0000-000000000004")]
    [InlineData("b0000000-0000-0000-0000-000000000005")]
    [InlineData("b0000000-0000-0000-0000-0000000000ff")]
    public async Task A_request_named_that_may_not_be_announced_is_a_failed_run_that_stages_nothing(string id)
    {
        string database = await RequestsAsync();

        (await RunAsync(database, Guid.Parse(id))).ShouldBe(1);

        (await StagedAsync(database)).ShouldBeEmpty();
    }

    [Fact]
    public async Task No_requests_is_a_run_with_nothing_to_stage()
    {
        string database = await MigrateAsync();

        (await RunAsync(database)).ShouldBe(0);

        (await StagedAsync(database)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_that_cannot_reach_the_database_exits_non_zero()
    {
        string absent = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"PrivacyRepublishAbsent{Guid.CreateVersion7():N}"
        }.ConnectionString;

        (await RunAsync(absent)).ShouldBe(1);
    }

    /// <summary>
    /// A migrated database holding a request closed by its last answer; one closed by an amendment removing a
    /// holder heard from before; one closed by an amendment removing a holder never heard from; one open; one
    /// overdue.
    /// </summary>
    private async Task<string> RequestsAsync()
    {
        string database = await MigrateAsync();

        await using PrivacyDbContext db = Open(database);

        string hash = new('a', 64);

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO privacy.ErasureRequests
                (RequestId, SubjectId, Status, RespondersCsv, RaisedAt, DueAt, ClosedAt, OverdueAt, Reissues,
                 SubjectHash)
            VALUES
                ({ClosedByAnswer}, NULL, 'Closed', 'ordering', {Raised}, {Raised}, {Answered}, NULL, 0, {hash}),
                ({ClosedByAmendment}, NULL, 'Closed', 'ordering', {Raised}, {Raised}, {Amended}, NULL, 0, {hash}),
                ({ClosedOverSilence}, NULL, 'Closed', 'ordering', {Raised}, {Raised}, {Amended}, NULL, 0, {hash}),
                ({StillOpen}, NEWID(), 'Open', 'ordering', {Raised}, {Raised}, NULL, NULL, 0, NULL),
                ({Overdue}, NEWID(), 'Overdue', 'ordering', {Raised}, {Raised}, NULL, {Answered}, 0, NULL);

            INSERT INTO privacy.ErasureCompletions (RequestId, Responder, Count, Counted, ReceivedAt)
            VALUES
                ({ClosedByAnswer}, 'ordering', 1, 1, {Answered}),
                ({ClosedByAmendment}, 'ordering', 1, 1, {Answered}),
                ({ClosedByAmendment}, 'shipping', 0, 0, {Answered}),
                ({ClosedByAmendment}, 'bff', 1, 1, {Amended}),
                ({ClosedOverSilence}, 'ordering', 1, 1, {Answered});

            INSERT INTO privacy.ErasureAmendments (RequestId, Sequence, Added, Removed, Reason, AmendedBy, AmendedAt)
            VALUES
                ({ClosedByAmendment}, 1, '', 'shipping', N'answered, then left the set', NEWID(), {Amended}),
                ({ClosedByAmendment}, 2, 'bff', '', N'added, so it removes nothing', NEWID(), {Amended}),
                ({ClosedOverSilence}, 1, '', 'bff', N'never answered', NEWID(), {Amended}),
                ({ClosedOverSilence}, 2, 'shipping', '', N'added, not removed', NEWID(), {Amended});
            """,
            TestContext.Current.CancellationToken);

        return database;
    }

    private async Task<string> MigrateAsync()
    {
        string database = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"PrivacyRepublish{Guid.CreateVersion7():N}"
        }.ConnectionString;

        (await ServiceFixture.RunMigratorAsync(database)).ShouldBe(0);

        return database;
    }

    private static async Task<int> RunAsync(string database, Guid? only = null)
    {
        string[] named = only is { } id ? [$"--Republish:Id={id}"] : [];

        string[] args =
        [
            $"--ConnectionStrings:Privacy={database}",
            .. MigratorRun.PlaintextTestContainers("Privacy"),
            "--Republish:Enabled=true",
            .. named
        ];

        using IHost host = MigratorHost.Build(args);
        using IServiceScope scope = host.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<PrivacyRepublisher>()
            .RunAsync(TestContext.Current.CancellationToken);
    }

    private static PrivacyDbContext Open(string database) =>
        new(new DbContextOptionsBuilder<PrivacyDbContext>().UseSqlServer(database).Options);

    /// <summary>Each staged row with its payload read as the dispatcher reads it (§9.4).</summary>
    private async Task<IReadOnlyList<(OutboxMessage Row, PersonalDataDeleteClosed Event)>> StagedAsync(
        string database)
    {
        await using PrivacyDbContext db = Open(database);

        OutboxMessage[] rows = await db.OutboxMessages
            .AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);

        return [.. rows.Select(row => (row, Read(row)))];
    }

    private PersonalDataDeleteClosed Read(OutboxMessage row) =>
        (PersonalDataDeleteClosed)JsonSerializer.Deserialize(
            row.Payload,
            fixture.MessageTypes.Resolve(row.MessageType),
            fixture.OutboxJson.Options)!;
}
