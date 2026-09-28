using Common.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Fulfilment;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// A booking the commit declines, over fakes rather than containers: which row
/// the unit reloads decides whether the carrier's booking is handed back, and a
/// real database cannot be made to reload a chosen state on cue.
/// </summary>
public sealed class SupersededBookingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_booking_the_commit_finds_voided_is_handed_back_under_the_cancel_key()
    {
        Shipment voided = Voided();
        RecordingCarrier carrier = new();
        RecordingLogger log = new();

        bool moved = await PassAsync(carrier, new InlineUnitOfWork(), log, voided);

        moved.ShouldBeFalse();
        CancellationRequest cancel = carrier.Cancels.ShouldHaveSingleItem(
            "spec section 6: a shipment voided while it was pending is never booked");
        cancel.IdempotencyKey.ShouldBe($"cancel:{voided.Id.Value}");
        cancel.Reference.ShouldBe(RecordingCarrier.Reference);
        log.Names().ShouldBe(["Superseded"]);
    }

    [Fact]
    public async Task A_booking_the_commit_finds_already_booked_is_kept()
    {
        ShipmentId id = new(Guid.CreateVersion7());
        OrderId order = new(Guid.CreateVersion7());

        // The execution strategy's retry after a commit whose acknowledgement
        // was lost: the first attempt books the pending row and commits, and
        // the second reloads that committed booking and declines to repeat it.
        Shipment pending = Shipment.For(id, order, Now);
        Shipment committed = Shipment.For(id, order, Now);
        committed.Book(RecordingCarrier.Reference, RecordingCarrier.TrackingNumber, Now).ShouldBeTrue();
        RecordingCarrier carrier = new();

        bool moved = await PassAsync(
            carrier, new LostAcknowledgementUnitOfWork(), new RecordingLogger(), pending, committed);

        carrier.Cancels.ShouldBeEmpty("a cancel here would withdraw the booking the row now holds");
        moved.ShouldBeTrue("the row holds this pass's booking");
    }

    [Fact]
    public async Task A_booking_commit_that_loses_to_the_cancel_consumer_is_repeated_and_handed_back()
    {
        // The consumer commits Voided between the pass's reload and its save,
        // so the first attempt books a pending row and its save conflicts.
        Shipment voided = Voided();
        Shipment pending = Shipment.For(voided.Id, voided.OrderId, Now);
        RecordingCarrier carrier = new();

        bool moved = await PassAsync(carrier, new ConflictOnceUnitOfWork(), new RecordingLogger(), pending, voided);

        moved.ShouldBeFalse();
        carrier.Cancels.ShouldHaveSingleItem(
                "a conflict left to the row's backoff strands a Voided row the claim never takes again")
            .IdempotencyKey.ShouldBe($"cancel:{voided.Id.Value}");
    }

    [Fact]
    public async Task A_carrier_too_late_to_take_the_booking_back_is_logged_as_an_orphan()
    {
        RecordingCarrier carrier = new() { CancelAnswer = () => new CancellationResult.TooLate() };
        RecordingLogger log = new();

        bool moved = await PassAsync(carrier, new InlineUnitOfWork(), log, Voided());

        moved.ShouldBeFalse();
        LogEntry orphaned = log.Entries.ShouldHaveSingleItem("a booking the carrier kept was not handed back");
        orphaned.Id.Name.ShouldBe("Orphaned");
        orphaned.Level.ShouldBe(LogLevel.Error);
        orphaned.Message.ShouldContain(RecordingCarrier.Reference);
    }

    [Fact]
    public async Task A_hand_back_the_carrier_never_answers_is_logged_as_an_orphan_and_not_thrown()
    {
        CarrierUnavailableException outage = new("carrier down");
        RecordingCarrier carrier = new() { CancelAnswer = () => throw outage };
        RecordingLogger log = new();

        // Not thrown, because the row's catch would back off a Voided row the
        // claim never takes again and report it as a row that will retry.
        bool moved = await PassAsync(carrier, new InlineUnitOfWork(), log, Voided());

        moved.ShouldBeFalse();
        LogEntry orphaned = log.Entries.ShouldHaveSingleItem();
        orphaned.Id.Name.ShouldBe("Orphaned");
        orphaned.Exception.ShouldBeSameAs(outage);
    }

    private static Shipment Voided()
    {
        Shipment shipment = Shipment.For(
            new ShipmentId(Guid.CreateVersion7()), new OrderId(Guid.CreateVersion7()), Now);
        shipment.Cancel(Now).ShouldBeTrue();

        return shipment;
    }

    private static async Task<bool> PassAsync(
        RecordingCarrier carrier,
        IUnitOfWork unitOfWork,
        RecordingLogger log,
        params Shipment[] loads)
    {
        ServiceCollection services = new();
        services.AddSingleton<ICarrierGateway>(carrier);
        services.AddSingleton(unitOfWork);
        services.AddSingleton<IShipmentRepository>(new ScriptedRepository(loads));
        services.AddSingleton<IDeliveryAddressStore>(new KnownAddress());
        services.AddSingleton(TimeProvider.System);

        await using ServiceProvider provider = services.BuildServiceProvider();
        FulfilmentWorker worker = new(provider.GetRequiredService<IServiceScopeFactory>(), log);

        Shipment first = loads[0];
        FulfilmentWork work = new(first.Id.Value, first.OrderId.Value, nameof(ShipmentStatus.Pending), null);

        return await worker.FulfilAsync(provider, work, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingCarrier : ICarrierGateway
    {
        public const string Reference = "crr_TEST";

        public const string TrackingNumber = "TRK-TEST";

        public List<CancellationRequest> Cancels { get; } = [];

        public Func<CancellationResult> CancelAnswer { get; init; } = () => new CancellationResult.Cancelled();

        public Task<BookingResult> BookAsync(BookingRequest request, CancellationToken ct) =>
            Task.FromResult<BookingResult>(new BookingResult.Booked(Reference, TrackingNumber));

        public Task<CancellationResult> CancelAsync(CancellationRequest request, CancellationToken ct)
        {
            Cancels.Add(request);
            return Task.FromResult(CancelAnswer());
        }

        public Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string reference, CancellationToken ct) =>
            throw new NotSupportedException("a fulfilment pass never reads the feed");
    }

    private sealed record LogEntry(LogLevel Level, EventId Id, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<FulfilmentWorker>
    {
        public List<LogEntry> Entries { get; } = [];

        public string?[] Names() => [.. Entries.Select(e => e.Id.Name)];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
    }

    /// <summary>Hands out the given loads in turn, the last of them for ever.</summary>
    private sealed class ScriptedRepository(Shipment[] loads) : IShipmentRepository
    {
        private int _next;

        public Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct) =>
            Task.FromResult<Shipment?>(loads[Math.Min(_next++, loads.Length - 1)]);

        public Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct) =>
            throw new NotSupportedException("a pass loads by the shipment it claimed");

        public void Add(Shipment shipment) =>
            throw new NotSupportedException("a pass never creates a shipment");
    }

    private sealed class KnownAddress : IDeliveryAddressStore
    {
        public Task SaveAsync(
            OrderId orderId,
            Guid customerId,
            DeliveryAddress address,
            DateTimeOffset fetchedAt,
            CancellationToken ct) =>
            throw new NotSupportedException("a stored address is not fetched again");

        public Task<DeliveryAddress?> GetAsync(OrderId orderId, CancellationToken ct) =>
            Task.FromResult<DeliveryAddress?>(new DeliveryAddress("1 Abay Avenue", null, "Almaty", "050000", "KZ"));
    }

    /// <summary>Runs the unit once and commits it, as a strategy with nothing to retry does.</summary>
    private class InlineUnitOfWork : IUnitOfWork
    {
        public bool HasActiveTransaction => false;

        public int ModifiedAggregateCount => 0;

        public virtual Task<TResult> ExecuteAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken ct) =>
            operation(ct);

        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);

        public Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct) =>
            throw new NotSupportedException("a pass writes through the aggregate");
    }

    /// <summary>
    /// The unit committed and its acknowledgement lost, so the strategy runs
    /// it again over what the first attempt committed (§6.3).
    /// </summary>
    private sealed class LostAcknowledgementUnitOfWork : InlineUnitOfWork
    {
        public override async Task<TResult> ExecuteAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken ct)
        {
            await operation(ct);
            return await operation(ct);
        }
    }

    /// <summary>
    /// The first unit's save loses to another writer's, as a row version
    /// refuses it; the strategy hands a conflict on rather than retrying it.
    /// </summary>
    private sealed class ConflictOnceUnitOfWork : InlineUnitOfWork
    {
        private bool _conflicted;

        public override async Task<TResult> ExecuteAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken ct)
        {
            TResult result = await operation(ct);

            if (_conflicted)
                return result;

            _conflicted = true;
            throw new DbUpdateConcurrencyException("the row version moved underneath the save");
        }
    }
}
