using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Persistence;
using Shipping.Infrastructure.Retention;
using Shipping.Infrastructure.Tracking;
using Shipping.Migrator;
using Shipping.OrderingStub;
using Common.Contracts.Ordering.V1;
using Common.TestSupport;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using Xunit;
// MassTransit names a Response as well; the carrier's builder is the one this file means.
using Response = WireMock.ResponseBuilders.Response;

namespace Shipping.TestSupport;

/// <summary>Shipping's names, migrator, worker host, carrier and address stub over the shared body (ADR-056).</summary>
public sealed class ServiceFixture()
    : ServiceFixture<ShippingWorkerFactory, Program, ShippingDbContext>("Shipping")
{
    /// <summary>How long a staged step may take; a deadline, not a sleep, so a prompt step costs nothing.</summary>
    private static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);

    private const string AttemptDueSql =
        "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE OrderId = {0} AND NextAttemptAt <= SYSDATETIMEOFFSET()";

    private const string PollDueSql =
        "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE OrderId = {0} AND NextPollAt <= SYSDATETIMEOFFSET()";

    /// <summary>The carrier, in process over the mappings Compose mounts (§14.1), behind a real HTTP hop.</summary>
    public WireMockServer Carrier { get; private set; } = null!;

    /// <summary>ADR-052's owner, a real gRPC server on loopback that the worker's address read meets.</summary>
    public StubOrdering Ordering { get; } = new();

    /// <summary>Fails the host's next commit that moves a shipment, once; disposing the fault disarms it.</summary>
    public CommitFault FailNextCommit() => Factory.CommitFaults.Arm();

    /// <summary>Every line the host has logged since the last reset.</summary>
    public CapturedLogs CapturedLogs => Factory.CapturedLogs;

    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Shipping",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    // The factory's retention defaults are the invented windows, so this
    // host runs as ADR-053 rule 2's made-up jurisdiction and no test opts in.
    protected override ShippingWorkerFactory CreateFactory() => NewWorkerHost(Carrier.Urls[0] + "/");

    /// <summary>Widens <c>shipping-svc</c>'s write to publish Ordering's events, which ADR-036 refuses.</summary>
    protected override string? HarnessWrite(string granted)
    {
        const string contracts = "Common\\.Contracts(";

        int anchor = granted.IndexOf(contracts, StringComparison.Ordinal);
        if (anchor < 0)
        {
            throw new InvalidOperationException(
                $"shipping-svc's write grant has no '{contracts}' alternation to add Ordering's exchanges to: "
                + granted);
        }

        return granted.Insert(anchor + contracts.Length, "\\.Ordering\\.V1:|");
    }

    protected override async Task StartStubsAsync()
    {
        Carrier = StartCarrier();
        await Ordering.InitializeAsync();
    }

    // A stub a test adds outlives a log reset, and a queued address or a logged line would reach the next test.
    protected override void ResetStubs()
    {
        Carrier.ResetMappings();
        Carrier.ReadStaticMappings(SimulatorMappings.Directory());
        Carrier.ResetLogEntries();
        Ordering.Reset();
        CapturedLogs.Clear();
    }

    protected override async ValueTask DisposeStubsAsync()
    {
        try
        {
            Carrier?.Stop();
        }
        finally
        {
            await Ordering.DisposeAsync();
        }
    }

    /// <summary>Runs exactly one fulfilment pass, with no timers and no waiting.</summary>
    public Task<int> RunFulfilmentPassAsync() =>
        Factory.Services
            .GetRequiredService<FulfilmentWorker>()
            .RunOnceAsync(TestContext.Current.CancellationToken);

    /// <summary>Runs exactly one tracking pass, with no timers and no waiting.</summary>
    public Task<int> RunTrackingPassAsync() =>
        Factory.Services.GetRequiredService<TrackingWorker>().ProcessBatchAsync(
            TestContext.Current.CancellationToken);

    /// <summary>A shipment booked through the real broker, <see cref="Ordering"/> and one fulfilment pass.</summary>
    public async Task<Shipment> BookedAsync(
        string postalCode,
        string country = "KZ",
        string? line1 = null,
        string? city = null)
    {
        Guid order = Guid.CreateVersion7();
        Ordering.Addresses[order] = new StubAddress(
            Guid.CreateVersion7(),
            line1 ?? "1 Abay Avenue",
            null,
            city ?? "Almaty",
            postalCode,
            country);

        OrderConfirmed confirmed = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = order,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = order,
            CustomerId = Guid.CreateVersion7(),
            TotalAmount = 10m,
            Currency = "KZT",
            Lines = [new ConfirmedLine(Guid.CreateVersion7(), 1, 10m)]
        };

        // Bounded, because a publish the broker refuses is retried rather than
        // failed, and an unbounded one would hold the run until CI kills it.
        using CancellationTokenSource bounded =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(StepDeadline);

        await Factory.Services.GetRequiredService<IBus>().Publish(
            confirmed,
            c =>
            {
                c.MessageId = confirmed.MessageId;
                c.CorrelationId = confirmed.CorrelationId;
            },
            bounded.Token);

        // The inbox row is written after the consumer's command has committed
        // (§9.5), so a pass run before it would find no shipment to claim.
        await WaitUntilAsync(async () => (await InboxAsync(confirmed.MessageId)).Count == 1);
        await WaitUntilAttemptDueAsync(order);

        if (await RunFulfilmentPassAsync() != 1)
        {
            throw new InvalidOperationException(
                $"The fulfilment pass did not book the shipment for postal code {postalCode}.");
        }

        await WaitUntilPollDueAsync(order);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IShipmentRepository>()
            .GetByOrderAsync(new OrderId(order), TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"Order {order} was booked and its shipment is now absent.");
    }

    /// <summary>Waits for the engine's clock to reach <c>NextAttemptAt</c>, which the host's clock stamped.</summary>
    public Task WaitUntilAttemptDueAsync(Guid order) =>
        WaitUntilAsync(async () => await ScalarAsync<int>(AttemptDueSql, order) == 1);

    /// <summary><see cref="WaitUntilAttemptDueAsync"/> for the tracking claim's <c>NextPollAt</c>.</summary>
    public Task WaitUntilPollDueAsync(Guid order) =>
        WaitUntilAsync(async () => await ScalarAsync<int>(PollDueSql, order) == 1);

    public Task<string> StatusAsync(ShipmentId id) =>
        ScalarAsync<string>("SELECT Value = Status FROM shipping.Shipments WHERE Id = {0}", id.Value);

    /// <summary>The reference the carrier answered a booking with, or null.</summary>
    public Task<string?> CarrierReferenceAsync(ShipmentId id) =>
        ScalarAsync<string?>("SELECT Value = CarrierReference FROM shipping.Shipments WHERE Id = {0}", id.Value);

    /// <summary>Null once a tracking pass has applied a page to a terminal shipment.</summary>
    public Task<DateTimeOffset?> NextPollAtAsync(ShipmentId id) =>
        ScalarAsync<DateTimeOffset?>("SELECT Value = NextPollAt FROM shipping.Shipments WHERE Id = {0}", id.Value);

    /// <summary>The fulfilment pass's failed passes on the row (ADR-054).</summary>
    public Task<int> AttemptsAsync(ShipmentId id) =>
        ScalarAsync<int>("SELECT Value = Attempts FROM shipping.Shipments WHERE Id = {0}", id.Value);

    /// <summary>The tracking pass's failed passes on the row (ADR-054).</summary>
    public Task<int> PollAttemptsAsync(ShipmentId id) =>
        ScalarAsync<int>("SELECT Value = PollAttempts FROM shipping.Shipments WHERE Id = {0}", id.Value);

    /// <summary>Moves <c>CreatedAt</c> back by <paramref name="age"/> from the host's clock.</summary>
    public Task AgeCreatedAsync(ShipmentId id, TimeSpan age) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET CreatedAt = {1} WHERE Id = {0};",
            id.Value,
            DateTimeOffset.UtcNow - age);

    /// <summary>Moves <c>CancellationRequestedAt</c> back by <paramref name="age"/> from the host's clock.</summary>
    public Task AgeCancellationAsync(ShipmentId id, TimeSpan age) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET CancellationRequestedAt = {1} WHERE Id = {0};",
            id.Value,
            DateTimeOffset.UtcNow - age);

    /// <summary>Null once a pass has released the row it claimed.</summary>
    public Task<DateTimeOffset?> LockedUntilAsync(ShipmentId id) =>
        ScalarAsync<DateTimeOffset?>("SELECT Value = LockedUntil FROM shipping.Shipments WHERE Id = {0}", id.Value);

    /// <summary>The engine's own clock, since the container's and the host's can disagree.</summary>
    public Task<DateTimeOffset> DatabaseNowAsync() =>
        ScalarAsync<DateTimeOffset>("SELECT Value = SYSDATETIMEOFFSET()");

    /// <summary>Repoints a booked row at a reference a test's own carrier answers.</summary>
    public Task SetCarrierReferenceAsync(ShipmentId id, string reference) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET CarrierReference = {0} WHERE Id = {1};",
            reference,
            id.Value);

    /// <summary>Stamps a live tracking lease on every pollable row, so a second pass meets a row in flight.</summary>
    public Task ClaimForTrackingAsync() =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET LockedUntil = DATEADD(second, {0}, SYSDATETIMEOFFSET()) " +
            "WHERE Status IN ('Booked', 'Dispatched');",
            TrackingWorker.LeaseSeconds);

    /// <summary>The same, under the fulfilment worker's lease and over its populations.</summary>
    public Task ClaimForFulfilmentAsync() =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET LockedUntil = DATEADD(second, {0}, SYSDATETIMEOFFSET()) " +
            "WHERE Status = 'Pending' " +
            "   OR (Status = 'Booked' AND CancellationRequestedAt IS NOT NULL AND CancellationRefusedAt IS NULL);",
            FulfilmentWorker.LeaseSeconds);

    /// <summary>Lapses every held lease, as a killed replica leaves it, not the NULL a release writes.</summary>
    public Task ExpireLeasesAsync() =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET LockedUntil = DATEADD(second, -1, SYSDATETIMEOFFSET()) " +
            "WHERE LockedUntil IS NOT NULL;");

    /// <summary>Stamps a cancellation request without asking the carrier, so the row is due to both workers.</summary>
    public Task RequestCancellationAsync(ShipmentId id) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET CancellationRequestedAt = SYSDATETIMEOFFSET() WHERE Id = {0};",
            id.Value);

    /// <summary>A shipment booked at a postal code whose simulated feed runs to delivered, and polled once.</summary>
    public async Task<Shipment> DeliveredAsync(string? line1 = null, string? city = null)
    {
        Shipment shipment = await BookedAsync("050000", line1: line1, city: city);

        if (await RunTrackingPassAsync() != 1)
        {
            throw new InvalidOperationException("The tracking pass did not poll the shipment for postal code 050000.");
        }

        string status = await StatusAsync(shipment.Id);

        if (!string.Equals(status, "Delivered", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The tracking pass left the shipment {status}, not Delivered.");
        }

        return shipment;
    }

    /// <summary>A voided shipment carrying a tracking event, so its address is due and its feed is not.</summary>
    public async Task<Shipment> VoidedWithTrackingAsync()
    {
        Shipment shipment = await BookedAsync("SIM-TRANSIT");

        await ExecuteAsync(
            """
            INSERT INTO shipping.TrackingEvents (ShipmentId, CarrierEventId, Status, OccurredAt, RecordedAt)
            VALUES ({0}, 'evt-in-transit', 'InTransit', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            shipment.Id.Value);

        await RequestCancellationAsync(shipment.Id);

        if (await RunFulfilmentPassAsync() != 1)
        {
            throw new InvalidOperationException("The fulfilment pass did not void the shipment.");
        }

        string status = await StatusAsync(shipment.Id);

        if (!string.Equals(status, "Voided", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The fulfilment pass left the shipment {status}, not Voided.");
        }

        return shipment;
    }

    /// <summary>Sets <c>TerminalAt</c> <paramref name="age"/> before the engine's clock, refusing a live row.</summary>
    public async Task AgeTerminalAsync(ShipmentId id, TimeSpan age)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        int updated = await db.Database.ExecuteSqlRawAsync(
            "UPDATE shipping.Shipments SET TerminalAt = DATEADD(second, {0}, SYSDATETIMEOFFSET()) " +
            "WHERE Id = {1} AND TerminalAt IS NOT NULL;",
            [-(int)age.TotalSeconds, id.Value],
            TestContext.Current.CancellationToken);

        if (updated != 1)
        {
            throw new InvalidOperationException($"Shipment {id.Value} is not terminal, so it cannot be aged.");
        }
    }

    /// <summary>How many delivery addresses are held for one order: one or none, as the table is keyed by it.</summary>
    public Task<int> AddressCountAsync(OrderId orderId) =>
        ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.DeliveryAddresses WHERE OrderId = {0}",
            orderId.Value);

    /// <summary>Stages delivered shipments past both windows, each with an address and a tracking event.</summary>
    public Task StageExpiredDeliveriesAsync(int count) =>
        ExecuteAsync(
            """
            CREATE TABLE #staged (ShipmentId uniqueidentifier NOT NULL, OrderId uniqueidentifier NOT NULL);

            INSERT INTO #staged (ShipmentId, OrderId)
            SELECT TOP ({0}) NEWID(), NEWID() FROM sys.all_objects a CROSS JOIN sys.all_objects b;

            INSERT INTO shipping.Shipments (Id, OrderId, Status, TerminalAt, Attempts, NextAttemptAt, CreatedAt)
            SELECT
                ShipmentId, OrderId, 'Delivered', DATEADD(day, -40, SYSDATETIMEOFFSET()), 0, SYSDATETIMEOFFSET(),
                DATEADD(day, -45, SYSDATETIMEOFFSET())
            FROM #staged;

            INSERT INTO shipping.DeliveryAddresses (OrderId, CustomerId, Line1, City, PostalCode, Country, FetchedAt)
            SELECT OrderId, NEWID(), '1 Test Street', 'Almaty', '050000', 'KZ', SYSDATETIMEOFFSET()
            FROM #staged;

            INSERT INTO shipping.TrackingEvents (ShipmentId, CarrierEventId, Status, OccurredAt, RecordedAt)
            SELECT ShipmentId, 'evt-delivered', 'Delivered', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
            FROM #staged;
            """,
            count);

    /// <summary>How many rows <c>shipping.Shipments</c> holds.</summary>
    public Task<int> ShipmentCountAsync() =>
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM shipping.Shipments");

    /// <summary>How many delivery addresses are held in all.</summary>
    public Task<int> AddressTotalAsync() =>
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM shipping.DeliveryAddresses");

    /// <summary>How many tracking events a shipment still holds.</summary>
    public Task<int> TrackingEventCountAsync(ShipmentId id) =>
        ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.TrackingEvents WHERE ShipmentId = {0}",
            id.Value);

    /// <summary>Runs exactly one statutory-retention pass, with no timers and no waiting.</summary>
    public Task<(int Addresses, int TrackingEvents)> PurgeShippingRetentionAsync() =>
        Factory.Services.GetRequiredService<ShippingRetentionService>().PurgeAsync(
            TestContext.Current.CancellationToken);

    /// <summary>Polls to <see cref="StepDeadline"/> and throws when it lapses.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StepDeadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The staged condition did not hold within {StepDeadline}.");
    }

    /// <summary>The exchanges bound to one queue, read from the broker itself.</summary>
    public async Task<string[]> BindingsAsync(string queue) =>
    [
        .. (await BrokerRowsAsync(["list_bindings", "source_name", "destination_name"]))
            .Where(columns => columns.Length == 2 && columns[1] == queue)
            .Select(columns => columns[0])
    ];

    /// <summary>A second worker host over these containers, with its own resilience pipeline.</summary>
    public ShippingWorkerFactory NewWorkerHost(string carrierBaseUrl) =>
        new(
            ConnectionString,
            BrokerConnectionString,
            carrierBaseUrl,
            addressSourceBaseUrl: Ordering.Address.ToString());

    /// <summary>Makes one carrier server answer one path with one status code, after an optional delay.</summary>
    public static IDisposable CarrierAnswers(
        WireMockServer server,
        string path,
        int statusCode,
        string method = "GET",
        TimeSpan? delay = null)
    {
        Guid id = Guid.CreateVersion7();
        IResponseBuilder answer = Response.Create().WithStatusCode(statusCode);

        server
            .Given(Request.Create().WithPath(new ExactMatcher(path)).UsingMethod(method))
            .AtPriority(0)
            .WithGuid(id)
            .RespondWith(delay is null ? answer : answer.WithDelay(delay.Value));

        return new CarrierMapping(server, id);
    }

    /// <summary>A carrier server over the simulator's mappings, on loopback so no firewall asks about it.</summary>
    public static WireMockServer StartCarrier()
    {
        WireMockServer server = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
        server.ReadStaticMappings(SimulatorMappings.Directory());

        return server;
    }

    /// <summary>The column names of one table, from the engine rather than from the model.</summary>
    public async Task<string[]> ColumnsAsync(string schema, string table)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        return await db.Database
            .SqlQuery<string>(
                $"""
                SELECT COLUMN_NAME AS Value
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = {schema} AND TABLE_NAME = {table}
                """)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds a prior attempt count through the column the fulfilment pass writes (ADR-054).</summary>
    public Task SetAttemptsAsync(ShipmentId id, int attempts) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET Attempts = {0} WHERE Id = {1};",
            attempts,
            id.Value);

    /// <summary><see cref="SetAttemptsAsync"/> for the tracking pass's own count (ADR-054).</summary>
    public Task SetPollAttemptsAsync(ShipmentId id, int attempts) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET PollAttempts = {0} WHERE Id = {1};",
            attempts,
            id.Value);

    /// <summary>Removes one mapping, so it cannot outlive its assertion.</summary>
    private sealed class CarrierMapping(WireMockServer server, Guid id) : IDisposable
    {
        public void Dispose() => server.DeleteMapping(id);
    }
}
