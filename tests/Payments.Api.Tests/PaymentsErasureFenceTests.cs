using System.Data.Common;
using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Privacy.ErasePersonalData;
using Payments.Infrastructure.Persistence;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>The erasure fences the order range; a late or reissued request spares what is not its.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PaymentsErasureFenceTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _subject = Guid.CreateVersion7();

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("DELETE FROM payments.PersonalDataErasures;");
    }

    public async ValueTask DisposeAsync() =>
        await fixture.ExecuteAsync("DELETE FROM payments.PersonalDataErasures;");

    [Fact]
    public async Task A_placement_waits_for_an_erasure_that_found_no_order_and_is_then_ended_by_its_audit_row()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid order = Guid.CreateVersion7();
        PauseBeforeAudit pause = new();

        await using PaymentsDbContext db = new(
            new DbContextOptionsBuilder<PaymentsDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .AddInterceptors(pause)
                .Options);
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        ErasePersonalDataHandler erasure = new(
            new SqlPaymentOrderStore(db),
            new PersonalDataErasureRepository(db),
            TimeProvider.System);

        await erasure.HandleAsync(new ErasePersonalDataCommand(Guid.CreateVersion7(), _subject), ct);
        Task saving = db.SaveChangesAsync(ct);
        (await pause.Reached.Task.WaitAsync(Deadline, ct))
            .ShouldBeTrue("the erasure has run its sweep and not yet written its audit row");

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Task placement = Handler(scope).HandleAsync(Placed(order, _subject), ct);

        await Should.ThrowAsync<TimeoutException>(() => placement.WaitAsync(Wait, ct));

        pause.Release.SetResult();
        await saving;
        await transaction.CommitAsync(ct);
        await placement.WaitAsync(Deadline, ct);

        (await PayerOfAsync(order)).ShouldBe(Guid.Empty, "the placement waited, then found the audit row");
    }

    [Fact]
    public async Task An_order_placed_under_the_request_being_run_keeps_its_payer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid request = Guid.CreateVersion7();
        Guid kept = Guid.CreateVersion7();
        Guid erased = Guid.CreateVersion7();
        await SeedAsync(kept, $"[\"{request}\"]");
        await SeedAsync(erased, $"[\"{Guid.CreateVersion7()}\"]");

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new ErasePersonalDataCommand(request, _subject), ct);

        (await PayerOfAsync(kept)).ShouldBe(_subject, "it was placed after this request, so the request is not its");
        (await PayerOfAsync(erased)).ShouldBe(Guid.Empty);
        (await ErasedUnderOfAsync(erased)).ShouldBe("[]", "the list names the buyer, so it goes with the payer");
        (await ErasedUnderOfAsync(kept)).ShouldBe($"[\"{request}\"]");
    }

    [Fact]
    public async Task A_late_placement_is_ended_and_an_order_placed_after_the_erasure_is_kept()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid late = Guid.CreateVersion7();
        Guid after = Guid.CreateVersion7();
        Guid request = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            "INSERT INTO payments.PersonalDataErasures (RequestId, SubjectHash, Count, ErasedAt) " +
            "VALUES ({0}, {1}, 0, {2})",
            request,
            PersonalDataErasure.HashSubject(request, _subject),
            At);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        await Handler(scope).HandleAsync(Placed(late, _subject, [Guid.CreateVersion7()]), ct);
        await Handler(scope).HandleAsync(Placed(after, _subject, [request]), ct);

        (await PayerOfAsync(late)).ShouldBe(Guid.Empty, "it lists another request, so the erasure is not older");
        (await ErasedUnderOfAsync(late)).ShouldBe("[]", "the list names the buyer, so it goes with the payer");
        (await PayerOfAsync(after)).ShouldBe(_subject);
    }

    private static IIntegrationEventHandler<OrderPlaced> Handler(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetServices<IIntegrationEventHandler<OrderPlaced>>().Single();

    private static OrderPlaced Placed(Guid order, Guid customer, IReadOnlyList<Guid>? erasedUnder = null) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = At,
        OrderId = order,
        CustomerId = customer,
        TotalAmount = 42.10m,
        Currency = "EUR",
        Lines = [new PlacedLine(Guid.CreateVersion7(), 1, 42.10m)],
        ErasedUnder = erasedUnder
    };

    private Task SeedAsync(Guid order, string erasedUnder) =>
        fixture.ExecuteAsync(
            "INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt, ErasedUnder) " +
            "VALUES ({0}, {1}, 42.10, 'EUR', SYSDATETIMEOFFSET(), {2})",
            order,
            _subject,
            erasedUnder);

    private Task<string> ErasedUnderOfAsync(Guid order) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = ErasedUnder FROM payments.PaymentOrders WHERE OrderId = {0}",
            order);

    private Task<Guid> PayerOfAsync(Guid order) =>
        fixture.ScalarAsync<Guid>("SELECT Value = CustomerId FROM payments.PaymentOrders WHERE OrderId = {0}", order);

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
            const string Audit = "INSERT INTO [payments].[PersonalDataErasures]";
            if (command.CommandText.Contains(Audit, StringComparison.Ordinal))
            {
                Reached.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
