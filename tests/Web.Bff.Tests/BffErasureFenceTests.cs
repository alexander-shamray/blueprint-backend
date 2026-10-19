using System.Data.Common;
using Common.Contracts.Privacy.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Common.Application;
using Shouldly;
using Web.Bff.Orders;
using Web.Bff.Persistence;
using Web.Bff.Privacy;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The erasure's order delete holds its range to the audit commit and keeps what is not its own.</summary>
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
