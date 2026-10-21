using System.Data.Common;
using Common.Contracts.Privacy.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Common.Application;
using Shouldly;
using Web.Bff.Addresses;
using Web.Bff.Orders;
using Web.Bff.Persistence;
using Web.Bff.Privacy;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The erasure's deletes hold their range to the audit commit and keep what is not their own.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class BffErasureFenceTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid Subject = new("dddddddd-dddd-dddd-dddd-dddddddddddd");

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("DELETE FROM bff.PersonalDataErasures;");
    }

    public async ValueTask DisposeAsync() =>
        await fixture.ExecuteAsync("DELETE FROM bff.PersonalDataErasures;");

    [Fact]
    public async Task A_projection_write_waits_for_an_erasure_that_found_no_order_and_is_then_deleted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid order = Guid.CreateVersion7();
        Guid request = Guid.CreateVersion7();
        PauseBeforeAudit pause = new();

        await using BffDbContext db = new(
            new DbContextOptionsBuilder<BffDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .AddInterceptors(pause)
                .Options);
        PersonalDataDeleteRequestedHandler handler = new(db, new NoReport(), TimeProvider.System);

        Task erasure = handler.HandleAsync(Requested(request), ct);
        (await pause.Reached.Task.WaitAsync(BffServiceFixture.StepDeadline, ct))
            .ShouldBeTrue("the erasure has run its delete and not yet written its audit row");

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Task projected = scope.ServiceProvider
            .GetServices<IIntegrationEventHandler<Common.Contracts.Ordering.V1.OrderPlaced>>()
            .Single(h => h is OrderProjection)
            .HandleAsync(OrderEvents.Placed(order, Subject, At), ct);

        await Should.ThrowAsync<TimeoutException>(() => projected.WaitAsync(Wait, ct));

        pause.Release.SetResult();
        await erasure;
        await projected.WaitAsync(BffServiceFixture.StepDeadline, ct);

        (await fixture.OrderAsync(order)).ShouldBeNull("the write waited, then found the audit row");
    }

    [Fact]
    public async Task A_save_waits_for_an_erasure_that_found_no_address_and_is_then_saved_under_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid request = Guid.CreateVersion7();
        PauseBeforeAudit pause = new();

        await using BffDbContext db = new(
            new DbContextOptionsBuilder<BffDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .AddInterceptors(pause)
                .Options);
        PersonalDataDeleteRequestedHandler handler = new(db, new NoReport(), TimeProvider.System);

        Task erasure = handler.HandleAsync(Requested(request), ct);
        (await pause.Reached.Task.WaitAsync(BffServiceFixture.StepDeadline, ct))
            .ShouldBeTrue("the erasure has run its delete and not yet written its audit row");

        Task saved = fixture.Factory.Services.GetRequiredService<SavedAddressStore>()
            .SaveAsync(Subject, new SavedAddress("1 Test Street", null, "Testville", "T1", "KZ"), ct);

        await Should.ThrowAsync<TimeoutException>(() => saved.WaitAsync(Wait, ct));

        pause.Release.SetResult();
        await erasure;
        await saved.WaitAsync(BffServiceFixture.StepDeadline, ct);

        (await fixture.ScalarAsync<string>(
            "SELECT Value = ErasedUnder FROM bff.SavedAddresses WHERE CustomerId = {0}",
            Subject)).ShouldBe($"[\"{request}\"]", "the save waited, then read the audit row and stamped it");
    }

    [Fact]
    public async Task An_order_placed_under_the_request_being_run_is_kept_and_not_recorded_as_erased()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid request = Guid.CreateVersion7();
        Guid kept = Guid.CreateVersion7();
        Guid erased = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            "INSERT INTO bff.Orders (OrderId, CustomerId, ErasedUnder, FirstSeenAt, AsOf) " +
            "VALUES ({0}, {1}, {2}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())",
            kept,
            Subject,
            $"[\"{request}\"]");
        await fixture.ExecuteAsync(
            "INSERT INTO bff.Orders (OrderId, CustomerId, ErasedUnder, FirstSeenAt, AsOf) " +
            "VALUES ({0}, {1}, N'[]', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())",
            erased,
            Subject);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        PersonalDataDeleteRequestedHandler handler = new(
            scope.ServiceProvider.GetRequiredService<BffDbContext>(),
            new NoReport(),
            TimeProvider.System);

        await handler.HandleAsync(Requested(request), ct);

        (await fixture.OrderAsync(kept)).ShouldNotBeNull("it was placed after this request, so the request is not its");
        (await fixture.OrderAsync(erased)).ShouldBeNull();
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM bff.ErasedOrders WHERE OrderId = {0}",
            kept)).ShouldBe(0);
    }

    [Fact]
    public async Task The_projection_stores_the_requests_an_order_was_placed_after()
    {
        Guid order = Guid.CreateVersion7();
        Guid request = Guid.CreateVersion7();
        Guid bystandersOrder = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IIntegrationEventHandler<Common.Contracts.Ordering.V1.OrderPlaced> projection = scope.ServiceProvider
            .GetServices<IIntegrationEventHandler<Common.Contracts.Ordering.V1.OrderPlaced>>()
            .Single(h => h is OrderProjection);

        CancellationToken ct = TestContext.Current.CancellationToken;
        await projection.HandleAsync(OrderEvents.Placed(order, Subject, At, [request]), ct);
        await projection.HandleAsync(OrderEvents.Placed(bystandersOrder, Guid.CreateVersion7(), At), ct);

        const string Sql = "SELECT Value = ErasedUnder FROM bff.Orders WHERE OrderId = {0}";
        (await fixture.ScalarAsync<string>(Sql, order)).ShouldBe($"[\"{request}\"]");
        (await fixture.ScalarAsync<string>(Sql, bystandersOrder)).ShouldBe("[]");
    }

    [Fact]
    public async Task A_reissue_that_finds_nothing_left_reports_what_the_first_pass_erased()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid request = Guid.CreateVersion7();
        RecordedReports reports = new();

        await fixture.ExecuteAsync(
            "INSERT INTO bff.Orders (OrderId, CustomerId, ErasedUnder, FirstSeenAt, AsOf) " +
            "VALUES ({0}, {1}, N'[]', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())",
            Guid.CreateVersion7(),
            Subject);

        // The second pass is the reissue a lost send is answered with (ADR-094).
        for (int pass = 0; pass < 2; pass++)
        {
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            PersonalDataDeleteRequestedHandler handler = new(
                scope.ServiceProvider.GetRequiredService<BffDbContext>(),
                reports,
                TimeProvider.System);

            await handler.HandleAsync(Requested(request), ct);
        }

        reports.Counts.ShouldBe([1, 1], "the reissue deletes nothing and reports the audit row's count");
    }

    private static PersonalDataDeleteRequested Requested(Guid request) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = request,
        OccurredAt = DateTimeOffset.UtcNow,
        RequestId = request,
        SubjectId = Subject
    };

    private sealed class NoReport : IErasureReporter
    {
        public Task ReportAsync(Guid requestId, int count, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordedReports : IErasureReporter
    {
        public List<int> Counts { get; } = [];

        public Task ReportAsync(Guid requestId, int count, CancellationToken ct)
        {
            Counts.Add(count);
            return Task.CompletedTask;
        }
    }

    /// <summary>Holds the erasure at its audit INSERT, after the deletes have run and before the row exists.</summary>
    private sealed class PauseBeforeAudit : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [bff].[PersonalDataErasures]", StringComparison.Ordinal))
            {
                Reached.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
