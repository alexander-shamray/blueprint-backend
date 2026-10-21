using System.Data.Common;
using System.Diagnostics.Metrics;
using Common.Application;
using Common.Contracts.Ordering.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Privacy.ErasePersonalData;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.Push;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The erasure fences the records' and contacts' ranges; a late event ends its own order only.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationsErasureFenceTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _subject = Guid.CreateVersion7();

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("DELETE FROM notifications.PersonalDataErasures;");
    }

    public async ValueTask DisposeAsync() =>
        await fixture.ExecuteAsync("DELETE FROM notifications.PersonalDataErasures;");

    [Fact]
    public async Task An_intake_waits_for_an_erasure_that_found_nothing_and_is_then_ended_by_its_audit_row()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid order = Guid.CreateVersion7();
        await using ErasureAtItsAudit erasure = await ErasureAtItsAudit.OpenAsync(fixture, _subject, ct);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Task intake = scope.ServiceProvider
            .GetServices<IIntegrationEventHandler<OrderPlaced>>()
            .Single()
            .HandleAsync(OrderEvents.Placed(order, _subject, At), ct);

        await Should.ThrowAsync<TimeoutException>(() => intake.WaitAsync(Wait, ct));

        await erasure.FinishAsync(ct);
        await intake.WaitAsync(ServiceFixture.StepDeadline, ct);

        (await fixture.OrderRecordAsync(order)).ShouldBeNull("the intake waited, then found the audit row");
        (await fixture.NotificationsAsync(order)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_customers_order_record_is_not_held_by_an_erasure()
    {
        // The erasure's HOLDLOCK read seeks its subject's keys on CustomerId's index; a scan would hold every row.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid order = Guid.CreateVersion7();

        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetServices<IIntegrationEventHandler<OrderPlaced>>()
                .Single()
                .HandleAsync(OrderEvents.Placed(order, Guid.CreateVersion7(), At), ct);
        }

        (await fixture.OrderRecordAsync(order)).ShouldNotBeNull("an update matching no row would wait on nothing");
        await using ErasureAtItsAudit erasure = await ErasureAtItsAudit.OpenAsync(fixture, _subject, ct);

        await fixture
            .ExecuteAsync("UPDATE notifications.OrderRecords SET CancelReason = N'probe' WHERE OrderId = {0}", order)
            .WaitAsync(Wait, ct);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = CancelReason FROM notifications.OrderRecords WHERE OrderId = {0}",
            order)).ShouldBe("probe");

        await erasure.FinishAsync(ct);
    }

    [Fact]
    public async Task The_erasures_first_statement_holds_the_subjects_own_records()
    {
        // The later statements delete the records, but a change committed before they run would stage a notice whose
        // erased check comes before the audit row; the seek on CustomerId's index alone leaves the row free.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid order = Guid.CreateVersion7();

        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetServices<IIntegrationEventHandler<OrderPlaced>>()
                .Single()
                .HandleAsync(OrderEvents.Placed(order, _subject, At), ct);
        }

        (await fixture.OrderRecordAsync(order)).ShouldNotBeNull("an update matching no row would wait on nothing");

        await using NotificationsDbContext db = new(
            new DbContextOptionsBuilder<NotificationsDbContext>().UseSqlServer(fixture.ConnectionString).Options);
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        SqlNotificationsPersonalDataStore store = new(
            db,
            new PushMetrics(fixture.Factory.Services.GetRequiredService<IMeterFactory>()));
        await store.DeleteWaitingNoticesAsync(_subject, Guid.CreateVersion7(), ct);

        Task change = fixture.ExecuteAsync(
            "UPDATE notifications.OrderRecords SET CancelReason = N'probe' WHERE OrderId = {0}",
            order);

        await Should.ThrowAsync<TimeoutException>(() => change.WaitAsync(Wait, ct));

        await transaction.RollbackAsync(ct);
        await change.WaitAsync(ServiceFixture.StepDeadline, ct);
    }

    [Fact]
    public async Task A_contact_save_waits_for_an_erasure_that_found_no_contact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ErasureAtItsAudit erasure = await ErasureAtItsAudit.OpenAsync(fixture, _subject, ct);

        Task save = fixture.StageContactAsync(_subject, "someone@example.test", "en", TimeSpan.Zero);

        await Should.ThrowAsync<TimeoutException>(() => save.WaitAsync(Wait, ct));

        await erasure.FinishAsync(ct);
        await save.WaitAsync(ServiceFixture.StepDeadline, ct);
    }

    [Theory]
    [InlineData("Placed")]
    [InlineData("Confirmed")]
    [InlineData("Cancelled")]
    public async Task A_late_event_for_an_order_placed_before_the_erasure_leaves_the_orders_placed_after_it(string name)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid before = Guid.CreateVersion7();
        Guid after = Guid.CreateVersion7();
        Guid request = await ErasedSubjectIntakeTests.RecordErasureAsync(fixture, _subject, At.AddDays(-1));

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        await services.GetServices<IIntegrationEventHandler<OrderPlaced>>().Single()
            .HandleAsync(OrderEvents.Placed(after, _subject, At, [request]), ct);

        switch (name)
        {
            case "Placed":
                await services.GetServices<IIntegrationEventHandler<OrderPlaced>>().Single()
                    .HandleAsync(OrderEvents.Placed(before, _subject, At), ct);
                break;
            case "Confirmed":
                await services.GetServices<IIntegrationEventHandler<OrderConfirmed>>().Single()
                    .HandleAsync(OrderEvents.Confirmed(before, _subject, At), ct);
                break;
            default:
                await services.GetServices<IIntegrationEventHandler<OrderCancelled>>().Single()
                    .HandleAsync(OrderEvents.Cancelled(before, _subject, At, CancelReasons.OutOfStock, null), ct);
                break;
        }

        (await fixture.OrderRecordAsync(before)).ShouldBeNull("the late event's own order is ended");
        (await fixture.NotificationsAsync(before)).ShouldBeEmpty();
        (await fixture.OrderRecordAsync(after)).ShouldNotBeNull("placed after the erasure, so ADR-099 keeps it");
        (await fixture.NotificationsAsync(after)).ShouldHaveSingleItem();
    }

    /// <summary>The erasure's statements in an open transaction, held at its audit INSERT.</summary>
    private sealed class ErasureAtItsAudit : IAsyncDisposable
    {
        private readonly NotificationsDbContext _db;
        private readonly IDbContextTransaction _transaction;
        private readonly PauseBeforeAudit _pause;
        private readonly Task _saving;

        private ErasureAtItsAudit(
            NotificationsDbContext db,
            IDbContextTransaction transaction,
            PauseBeforeAudit pause,
            Task saving)
        {
            _db = db;
            _transaction = transaction;
            _pause = pause;
            _saving = saving;
        }

        public static async Task<ErasureAtItsAudit> OpenAsync(
            ServiceFixture fixture,
            Guid subject,
            CancellationToken ct)
        {
            PauseBeforeAudit pause = new();
            NotificationsDbContext db = new(
                new DbContextOptionsBuilder<NotificationsDbContext>()
                    .UseSqlServer(fixture.ConnectionString)
                    .AddInterceptors(pause)
                    .Options);
            IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);

            ErasePersonalDataHandler handler = new(
                new SqlNotificationsPersonalDataStore(
                    db,
                    new PushMetrics(fixture.Factory.Services.GetRequiredService<IMeterFactory>())),
                new PersonalDataErasureRepository(db),
                TimeProvider.System);

            await handler.HandleAsync(new ErasePersonalDataCommand(Guid.CreateVersion7(), subject), ct);
            Task saving = db.SaveChangesAsync(ct);
            (await pause.Reached.Task.WaitAsync(ServiceFixture.StepDeadline, ct))
                .ShouldBeTrue("the erasure has run its deletes and not yet written its audit row");

            return new ErasureAtItsAudit(db, transaction, pause, saving);
        }

        public async Task FinishAsync(CancellationToken ct)
        {
            _pause.Release.TrySetResult();
            await _saving;
            await _transaction.CommitAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            _pause.Release.TrySetResult();
            await _transaction.DisposeAsync();
            await _db.DisposeAsync();
        }
    }

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
            const string Audit = "INSERT INTO [notifications].[PersonalDataErasures]";
            if (command.CommandText.Contains(Audit, StringComparison.Ordinal))
            {
                Reached.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
