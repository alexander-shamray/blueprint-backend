using System.Globalization;
using Common.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Fulfilment;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>A pass whose commit declines, over fakes, as a real database cannot reload a chosen state.</summary>
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

        // The strategy's retry after a lost acknowledgement reloads the committed booking and declines to repeat it.
        Shipment pending = Shipment.For(id, order, Now);
        Shipment committed = Shipment.For(id, order, Now);
        committed.Book(RecordingCarrier.Reference, RecordingCarrier.TrackingNumber, Now).ShouldBeTrue();
        RecordingCarrier carrier = new();

        bool moved = await PassAsync(
            carrier,
            new LostAcknowledgementUnitOfWork(),
            new RecordingLogger(),
            pending,
            committed);

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
        carrier.Cancels
            .ShouldHaveSingleItem(
                "a conflict left to the row's backoff strands a Voided row the claim never takes again")
            .IdempotencyKey.ShouldBe($"cancel:{voided.Id.Value}");
    }

    [Fact]
    public async Task A_refusal_commit_that_loses_to_the_cancel_consumer_ends_quietly_on_the_voided_row()
    {
        Shipment voided = Voided();
        Shipment pending = Shipment.For(voided.Id, voided.OrderId, Now);
        RecordingCarrier carrier = new() { BookAnswer = () => new BookingResult.Refused("address_not_serviceable") };
        RecordingLogger log = new();

        // Returned rather than thrown: the row's catch would back off a Voided
        // row the claim never takes again and log it as a row that will retry.
        bool moved = await PassAsync(carrier, new ConflictOnceUnitOfWork(), log, pending, voided);

        moved.ShouldBeFalse();
        log.Entries.ShouldBeEmpty();
        carrier.Cancels.ShouldBeEmpty("a refused booking holds nothing at the carrier to hand back");
    }

    [Fact]
    public async Task An_unknown_order_commit_that_loses_to_the_cancel_consumer_ends_quietly_on_the_voided_row()
    {
        Shipment voided = Voided();
        Shipment pending = Shipment.For(voided.Id, voided.OrderId, Now);
        RecordingCarrier carrier = new();
        RecordingLogger log = new();

        bool moved = await PassAsync(
            new NoStoredAddress(),
            carrier,
            new ConflictOnceUnitOfWork(),
            log,
            pending,
            voided);

        moved.ShouldBeFalse();
        log.Entries.ShouldBeEmpty();
        carrier.Bookings.ShouldBe(0, "an order the owner does not know is never booked");
    }

    [Fact]
    public async Task A_booking_the_carrier_is_too_late_to_take_back_is_kept_for_the_claim_to_ask_again()
    {
        Shipment voided = Voided();
        RecordingCarrier carrier = new() { CancelAnswer = () => new CancellationResult.TooLate() };
        RecordingLogger log = new();

        bool moved = await PassAsync(carrier, new InlineUnitOfWork(), log, voided);

        moved.ShouldBeFalse();
        ShouldBeKeptForTheClaim(voided);
        LogEntry deferred = log.Entries.ShouldHaveSingleItem("the booking is on the row, not only in a log line");
        deferred.Id.Name.ShouldBe("HandBackDeferred");
        deferred.Level.ShouldBe(LogLevel.Warning);
        deferred.Message.ShouldContain(RecordingCarrier.Reference);
    }

    [Fact]
    public async Task A_hand_back_the_carrier_never_answers_is_kept_for_the_claim_and_not_thrown()
    {
        Shipment voided = Voided();
        CarrierUnavailableException outage = new("carrier down");
        RecordingCarrier carrier = new() { CancelAnswer = () => throw outage };
        RecordingLogger log = new();

        // Not thrown: the row is kept as a booked one whose cancellation the claim retries on its own ladder.
        bool moved = await PassAsync(carrier, new InlineUnitOfWork(), log, voided);

        moved.ShouldBeFalse();
        ShouldBeKeptForTheClaim(voided);
        LogEntry deferred = log.Entries.ShouldHaveSingleItem();
        deferred.Id.Name.ShouldBe("HandBackDeferred");
        deferred.Exception.ShouldBeSameAs(outage);
    }

    [Fact]
    public async Task A_hand_back_that_cannot_even_be_kept_is_logged_as_an_orphan()
    {
        Shipment voided = Voided();
        RecordingCarrier carrier = new() { CancelAnswer = () => new CancellationResult.TooLate() };
        RecordingLogger log = new();

        bool moved = await PassAsync(carrier, new FailingSecondUnitOfWork(), log, voided);

        moved.ShouldBeFalse();
        LogEntry orphaned = log.Entries.ShouldHaveSingleItem("only a booking neither handed back nor kept is lost");
        orphaned.Id.Name.ShouldBe("Orphaned");
        orphaned.Level.ShouldBe(LogLevel.Error);
        orphaned.Message.ShouldContain(RecordingCarrier.Reference);
    }

    // What the fulfilment claim takes as a booked row to cancel, dated from the void.
    private static void ShouldBeKeptForTheClaim(Shipment shipment)
    {
        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.CarrierReference.ShouldBe(RecordingCarrier.Reference);
        shipment.CancellationRequestedAt.ShouldBe(Now, "the order was cancelled when the row was voided");
        shipment.CancellationRefusedAt.ShouldBeNull();
        shipment.TerminalAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_booking_whose_commit_fails_is_logged_with_its_carrier_reference_and_rethrown()
    {
        Shipment pending = Shipment.For(new ShipmentId(Guid.CreateVersion7()), new OrderId(Guid.CreateVersion7()), Now);
        RecordingCarrier carrier = new();
        RecordingLogger log = new();

        RetryLimitExceededException thrown = await Should.ThrowAsync<RetryLimitExceededException>(
            () => PassAsync(carrier, new ExhaustedUnitOfWork(), log, pending));

        LogEntry uncommitted = log.Entries.ShouldHaveSingleItem(
            "the cancel consumer can void the row before a pass books it again");
        uncommitted.Id.Name.ShouldBe("BookingUncommitted");
        uncommitted.Level.ShouldBe(LogLevel.Error);
        uncommitted.Message.ShouldContain(RecordingCarrier.Reference);
        uncommitted.Message.ShouldContain(pending.Id.Value.ToString());
        uncommitted.Message.ShouldContain(pending.OrderId.Value.ToString());
        uncommitted.Message.ShouldNotContain("Abay");
        uncommitted.Exception.ShouldBeSameAs(thrown);
        carrier.Cancels.ShouldBeEmpty("a pending row's booking is rebooked under the same key, not handed back");
    }

    private static Shipment Voided()
    {
        Shipment shipment = Shipment.For(
            new ShipmentId(Guid.CreateVersion7()),
            new OrderId(Guid.CreateVersion7()),
            Now);
        shipment.Cancel(Now).ShouldBeTrue();

        return shipment;
    }

    private static Task<bool> PassAsync(
        RecordingCarrier carrier,
        IUnitOfWork unitOfWork,
        RecordingLogger log,
        params Shipment[] loads) =>
        PassAsync(new KnownAddress(), carrier, unitOfWork, log, loads);

    private static async Task<bool> PassAsync(
        IDeliveryAddressStore store,
        RecordingCarrier carrier,
        IUnitOfWork unitOfWork,
        RecordingLogger log,
        params Shipment[] loads)
    {
        ServiceCollection services = new();
        services.AddSingleton<ICarrierGateway>(carrier);
        services.AddSingleton(unitOfWork);
        services.AddSingleton<IShipmentRepository>(new ScriptedRepository(loads));
        services.AddSingleton(store);
        services.AddSingleton<IDeliveryAddressSource>(new UnknownOrder());
        services.AddSingleton(TimeProvider.System);
        // Options.Create bypasses validation, so a far-off age keeps these
        // fixed-instant shipments inside it however long the suite runs.
        services.AddSingleton(Options.Create(new FulfilmentOptions
        {
            GiveUpAge = TimeSpan.Parse(FulfilmentOptions.MaximumGiveUpAge, CultureInfo.InvariantCulture)
        }));

        await using ServiceProvider provider = services.BuildServiceProvider();
        FulfilmentWorker worker = new(provider.GetRequiredService<IServiceScopeFactory>(), log);

        Shipment first = loads[0];
        FulfilmentWork work = new(
            first.Id.Value,
            first.OrderId.Value,
            nameof(ShipmentStatus.Pending),
            null,
            first.CreatedAt,
            null,
            null,
            null,
            first.CreatedAt.AddSeconds(FulfilmentWorker.LeaseSeconds));

        return await worker.FulfilAsync(provider, work, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingCarrier : ICarrierGateway
    {
        public const string Reference = "crr_TEST";

        public const string TrackingNumber = "TRK-TEST";

        public List<CancellationRequest> Cancels { get; } = [];

        public int Bookings { get; private set; }

        public Func<CancellationResult> CancelAnswer { get; init; } = () => new CancellationResult.Cancelled();

        public Func<BookingResult> BookAnswer { get; init; } =
            () => new BookingResult.Booked(Reference, TrackingNumber);

        public Task<BookingResult> BookAsync(BookingRequest request, CancellationToken ct)
        {
            Bookings++;
            return Task.FromResult(BookAnswer());
        }

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

    private sealed class NoStoredAddress : IDeliveryAddressStore
    {
        public Task SaveAsync(
            OrderId orderId,
            Guid customerId,
            DeliveryAddress address,
            DateTimeOffset fetchedAt,
            CancellationToken ct) =>
            throw new NotSupportedException("an order the owner does not know has no address to store");

        public Task<DeliveryAddress?> GetAsync(OrderId orderId, CancellationToken ct) =>
            Task.FromResult<DeliveryAddress?>(null);
    }

    private sealed class UnknownOrder : IDeliveryAddressSource
    {
        public Task<AddressLookup> GetAsync(OrderId orderId, CancellationToken ct) =>
            Task.FromResult<AddressLookup>(new AddressLookup.NoSuchOrder());
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

    /// <summary>The unit committed and its acknowledgement lost, so the strategy runs it again (§6.3).</summary>
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

    /// <summary>The second unit of work fails outright, as a database that went away between the two would.</summary>
    private sealed class FailingSecondUnitOfWork : InlineUnitOfWork
    {
        private int _units;

        public override Task<TResult> ExecuteAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken ct) =>
            ++_units == 2
                ? throw new InvalidOperationException("the database refused the second unit")
                : operation(ct);
    }

    /// <summary>The first unit's save loses to another writer's, as a row version refuses it.</summary>
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

    /// <summary>Every attempt at the unit meets a transient fault and the strategy gives up.</summary>
    private sealed class ExhaustedUnitOfWork : InlineUnitOfWork
    {
        public override Task<TResult> ExecuteAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken ct) =>
            throw new RetryLimitExceededException("the strategy's retries ran out", new TimeoutException());
    }
}
