using System.Data.Common;
using System.Text.Json;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Persistence;
using Shipping.Infrastructure.Retention;
using Shipping.Infrastructure.Tracking;
using Shipping.Migrator;
using Shipping.OrderingStub;
using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DotNet.Testcontainers.Containers;
using Respawn;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using Xunit;
// MassTransit names a Response as well; the carrier's builder is the one this file means.
using Response = WireMock.ResponseBuilders.Response;

namespace Shipping.TestSupport;

/// <summary>A real SQL Server migrated by the real migrator, a real broker and the simulator (§12.4).</summary>
public sealed class ServiceFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    // Built in InitializeAsync, because its mappings resolve through BrokerContextPath, which can throw.
    private RabbitMqContainer? _rabbit;

    private Respawner? _respawner;

    /// <summary>How long a staged step may take; a deadline, not a sleep, so a prompt step costs nothing.</summary>
    private static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);

    private const string AttemptDueSql =
        "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE OrderId = {0} AND NextAttemptAt <= SYSDATETIMEOFFSET()";

    private const string PollDueSql =
        "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE OrderId = {0} AND NextPollAt <= SYSDATETIMEOFFSET()";

    /// <summary>Widens <c>shipping-svc</c>'s write to publish Ordering's events, which ADR-036 refuses.</summary>
    private async Task WidenWriteForTheHarnessAsync()
    {
        const string user = "shipping-svc";
        const string contracts = "Common\\.Contracts(";

        (string configure, string granted, string read) = ImportedGrant();

        int anchor = granted.IndexOf(contracts, StringComparison.Ordinal);
        if (anchor < 0)
        {
            throw new InvalidOperationException(
                $"{user}'s write grant has no '{contracts}' alternation to add Ordering's exchanges to: {granted}");
        }

        string write = granted.Insert(anchor + contracts.Length, "\\.Ordering\\.V1:|");

        ExecResult result = await _rabbit!.ExecAsync(
            ["rabbitmqctl", "set_permissions", "-p", "/", user, configure, write, read],
            TestContext.Current.CancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not widen {user}'s broker permissions for the harness "
                + $"(exit {result.ExitCode}). stdout: {result.Stdout} stderr: {result.Stderr}");
        }

        // The mapped file rather than the container, because it is the same
        // text the broker imported and it can be read before anything starts.
        static (string Configure, string Write, string Read) ImportedGrant()
        {
            string path = Path.Combine(BrokerContextPath(), "definitions.json");
            using JsonDocument definitions = JsonDocument.Parse(File.ReadAllText(path));

            foreach (JsonElement entry in definitions.RootElement.GetProperty("permissions").EnumerateArray())
            {
                if (entry.GetProperty("user").GetString() != user || entry.GetProperty("vhost").GetString() != "/")
                    continue;

                return (
                    entry.GetProperty("configure").GetString()!,
                    entry.GetProperty("write").GetString()!,
                    entry.GetProperty("read").GetString()!);
            }

            throw new InvalidOperationException(
                $"{path} grants {user} nothing on the default vhost, so there is no scope to preserve.");
        }
    }

    /// <summary>Shipping's own database (§7.1), not the container's <c>master</c>.</summary>
    public string ConnectionString { get; private set; } = null!;

    public ShippingWorkerFactory Factory { get; private set; } = null!;

    /// <summary>The carrier, in process over the mappings Compose mounts (§14.1), behind a real HTTP hop.</summary>
    public WireMockServer Carrier { get; private set; } = null!;

    /// <summary>ADR-052's owner, a real gRPC server on loopback that the worker's address read meets.</summary>
    public StubOrdering Ordering { get; } = new();

    /// <summary>Fails the host's next commit that moves a shipment, once; disposing the fault disarms it.</summary>
    public CommitFault FailNextCommit() => Factory.CommitFaults.Arm();

    /// <summary>Every line the host has logged since the last reset.</summary>
    public CapturedLogs CapturedLogs => Factory.CapturedLogs;

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
            Guid.CreateVersion7(), line1 ?? "1 Abay Avenue", null, city ?? "Almaty", postalCode, country);

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
            "UPDATE shipping.Shipments SET CreatedAt = {1} WHERE Id = {0};", id.Value, DateTimeOffset.UtcNow - age);

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
            _rabbit!.GetConnectionString(),
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

    /// <summary>One <c>rabbitmqctl</c> listing, split into its tab-separated columns.</summary>
    private async Task<IReadOnlyList<string[]>> BrokerRowsAsync(string[] listing)
    {
        ExecResult result = await _rabbit!.ExecAsync(
            ["rabbitmqctl", listing[0], "--quiet", "--no-table-headers", .. listing[1..]],
            TestContext.Current.CancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not run the broker's {listing[0]} (exit {result.ExitCode}). stderr: {result.Stderr}");
        }

        return
        [
            .. result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t', StringSplitOptions.TrimEntries))
        ];
    }

    /// <summary>Removes one mapping, so it cannot outlive its assertion; <c>ResetAsync</c> is the backstop.</summary>
    private sealed class CarrierMapping(WireMockServer server, Guid id) : IDisposable
    {
        public void Dispose() => server.DeleteMapping(id);
    }

    /// <summary>The exit code of the first real migration run.</summary>
    public int FirstRunExitCode { get; private set; } = -1;

    private static string BrokerContextPath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                continue;

            string context = Path.Combine(dir.FullName, "deploy", "compose", "rabbitmq");
            if (!File.Exists(Path.Combine(context, "Dockerfile")))
            {
                throw new InvalidOperationException(
                    $"Found the solution at {dir.FullName} but no Dockerfile at {context} (§14.1, ADR-021).");
            }

            return context;
        }

        throw new InvalidOperationException(
            $"No Platform.slnx above {AppContext.BaseDirectory}; the broker image cannot be built.");
    }

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync()
    {
        // The password is §14.1's local-development default.
        _rabbit = new RabbitMqBuilder()
            .WithImage("rabbitmq:4.1-management-alpine")
            .WithUsername("shipping-svc")
            .WithPassword("local-dev-shipping")
            .WithResourceMapping(
                new FileInfo(Path.Combine(BrokerContextPath(), "definitions.json")),
                "/etc/rabbitmq/")
            .WithResourceMapping(
                new FileInfo(Path.Combine(BrokerContextPath(), "20-commerce.conf")),
                "/etc/rabbitmq/conf.d/")
            .Build();

        await Task.WhenAll(
            _sql.StartAsync(TestContext.Current.CancellationToken),
            _rabbit.StartAsync(TestContext.Current.CancellationToken));

        await WidenWriteForTheHarnessAsync();

        Carrier = StartCarrier();
        await Ordering.InitializeAsync();

        // The container hands out master; Shipping owns a database of its own (§7.1), which MigrateAsync creates.
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = "Shipping";
        ConnectionString = connection.ConnectionString;

        FirstRunExitCode = await RunMigratorAsync(ConnectionString);

        // The factory's retention defaults are the invented windows, so this
        // host runs as ADR-053 rule 2's made-up jurisdiction and no test opts in.
        Factory = NewWorkerHost(Carrier.Urls[0] + "/");

        // A table of the test, not a migration, so it never ships to production.
        await ExecuteAsync(
            """
            CREATE TABLE shipping.TransactionProbe
            (
                Id   uniqueidentifier NOT NULL PRIMARY KEY,
                Note nvarchar(100)    NOT NULL
            );
            """);
    }

    /// <summary>§12.4's reset: truncates the <c>shipping</c> schema and restores the simulator's mappings.</summary>
    public async Task ResetAsync()
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // dbo is excluded, so EF's migration history survives the truncation.
        _respawner ??= await Respawner.CreateAsync(
            connection,
            new RespawnerOptions
            {
                DbAdapter = DbAdapter.SqlServer,
                SchemasToInclude = ["shipping"]
            });

        await _respawner.ResetAsync(connection);

        // A stub a test adds outlives a log reset, and a queued address or a logged line would reach the next test.
        Carrier.ResetMappings();
        Carrier.ReadStaticMappings(SimulatorMappings.Directory());
        Carrier.ResetLogEntries();
        Ordering.Reset();
        CapturedLogs.Clear();
    }

    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static async Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null)
    {
        string[] args =
        [
            .. Setting("ConnectionStrings:ShippingMigrator", migratorConnectionString),
            .. Setting("ConnectionStrings:Shipping", runtimeConnectionString)
        ];

        using IHost host = MigratorHost.Build(args);
        using IServiceScope scope = host.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<MigrationRunner>()
            .RunAsync(TestContext.Current.CancellationToken);

        static string[] Setting(string key, string? value) =>
            value is null ? [] : [$"--{key}={value}"];
    }

    /// <summary>Runs a statement outside any unit of work; a <c>{0}</c> placeholder is a SQL parameter.</summary>
    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        await db.Database.ExecuteSqlRawAsync(sql, parameters, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads one scalar outside any unit of work; a <c>{0}</c> placeholder is a SQL parameter.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        return await db.Database
            .SqlQueryRaw<T>(sql, parameters)
            .SingleAsync(TestContext.Current.CancellationToken);
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

    /// <summary>The migrations EF considers applied, asked through EF rather than its history table.</summary>
    public async Task<string[]> AppliedMigrationsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        return [.. await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)];
    }

    /// <summary>The host's own map (§9.4), which <see cref="Outbox.OutboxRows"/> stages through.</summary>
    public MessageTypeMap MessageTypes =>
        Factory.Services.GetRequiredService<MessageTypeMap>();

    /// <summary>The host's payload format, so a staged row is written the way the dispatcher reads it.</summary>
    public OutboxJson OutboxJson =>
        Factory.Services.GetRequiredService<OutboxJson>();

    /// <summary>Runs exactly one claim-and-deliver pass, with no timers and no waiting.</summary>
    public Task<int> ProcessOutboxBatchAsync() =>
        Factory.Services
            .GetRequiredService<OutboxDispatcher>()
            .ProcessBatchAsync(TestContext.Current.CancellationToken);

    /// <summary>Every outbox row, untracked, for asserting over.</summary>
    public async Task<IReadOnlyList<OutboxMessage>> OutboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes rows directly, for tests about the dispatcher rather than the staging.</summary>
    public async Task StageOutboxAsync(params OutboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        db.OutboxMessages.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds a prior attempt count through the same column the dispatcher writes.</summary>
    public Task SetOutboxAttemptsAsync(Guid messageId, int attempts) =>
        ExecuteAsync(
            "UPDATE shipping.OutboxMessages SET Attempts = {0} WHERE MessageId = {1};",
            attempts,
            messageId);

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

    /// <summary>Repoints a row's lane through SQL, making a row <see cref="OutboxMessage.Stage"/> refuses.</summary>
    public Task SetOutboxLaneAsync(Guid messageId, OutboxLane lane) =>
        ExecuteAsync(
            "UPDATE shipping.OutboxMessages SET Lane = {0} WHERE MessageId = {1};",
            lane.ToString(),
            messageId);

    /// <summary>Clears retry backoff leases, so the next pass is gated only by the attempt cap.</summary>
    public Task ExpireOutboxLeasesAsync() =>
        ExecuteAsync("UPDATE shipping.OutboxMessages SET LockedUntil = NULL WHERE ProcessedAt IS NULL;");

    /// <summary>Every inbox row, untracked, for asserting over (§9.5).</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        return await db.InboxMessages
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The inbox rows one message wrote, untracked (§9.5), so other tests' rows are no part of it.</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync(Guid messageId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        return await db.InboxMessages
            .AsNoTracking()
            .Where(m => m.MessageId == messageId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Every idempotency marker, untracked, for asserting over (§8.5).</summary>
    public async Task<IReadOnlyList<IdempotencyMarker>> IdempotencyMarkersAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        return await db.IdempotencyMarkers
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes inbox rows directly, for tests about the purge rather than the filter.</summary>
    public async Task StageInboxAsync(params InboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        db.InboxMessages.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes idempotency markers directly, for tests about the purge rather than §8.5.</summary>
    public async Task StageIdempotencyMarkersAsync(params IdempotencyMarker[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        db.IdempotencyMarkers.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The registered claim store, which never holds a key (ADR-039).</summary>
    public IIdempotencyStore IdempotencyClaims =>
        Factory.Services.GetRequiredService<IIdempotencyStore>();

    /// <summary>Ages a processed outbox row, so a retention test reaches the window without a fake clock.</summary>
    public Task SetOutboxProcessedAtAsync(Guid messageId, DateTimeOffset processedAt) =>
        ExecuteAsync(
            "UPDATE shipping.OutboxMessages SET ProcessedAt = {0} WHERE MessageId = {1};",
            processedAt,
            messageId);

    /// <summary>Runs exactly one retention pass over every table, with no timers and no waiting.</summary>
    public Task<(int Outbox, int Inbox, int Idempotency)> PurgeRetentionAsync() =>
        Factory.Services
            .GetRequiredService<RetentionPurgeService>()
            .PurgeAsync(TestContext.Current.CancellationToken);

    /// <summary>One pass under a policy of the test's own, for batching edges the registered one cannot show.</summary>
    public Task<(int Outbox, int Inbox, int Idempotency)> PurgeWithAsync(RetentionPolicy policy) =>
        PurgeWithAsync(policy, Factory.Services.GetRequiredService<IIdempotencyStore>());

    /// <summary>The same pass with the claim store substituted, so a test can hold a key (ADR-039).</summary>
    public Task<(int Outbox, int Inbox, int Idempotency)> PurgeWithAsync(
        RetentionPolicy policy,
        IIdempotencyStore claims)
    {
        RetentionPurgeService purge = new(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Factory.Services.GetRequiredService<OutboxTable>(),
            Factory.Services.GetRequiredService<InboxTable>(),
            Factory.Services.GetRequiredService<IdempotencyMarkerTable>(),
            claims,
            policy,
            Factory.Services.GetRequiredService<ILogger<RetentionPurgeService>>());

        return purge.PurgeAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// One pass with the registered clock moved by <paramref name="skew"/> and the server's, which ages a
    /// marker (ADR-038), left alone.
    /// </summary>
    public Task<(int Outbox, int Inbox, int Idempotency)> PurgeWithSkewedClockAsync(
        RetentionPolicy policy,
        TimeSpan skew)
    {
        RetentionPurgeService purge = new(
            new SkewedScopeFactory(
                Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                new SkewedClock(skew)),
            Factory.Services.GetRequiredService<OutboxTable>(),
            Factory.Services.GetRequiredService<InboxTable>(),
            Factory.Services.GetRequiredService<IdempotencyMarkerTable>(),
            Factory.Services.GetRequiredService<IIdempotencyStore>(),
            policy,
            Factory.Services.GetRequiredService<ILogger<RetentionPurgeService>>());

        return purge.PurgeAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Running rather than frozen, because the pass compares against rows staged in real time.</summary>
    private sealed class SkewedClock(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + skew;
    }

    private sealed class SkewedScopeFactory(IServiceScopeFactory inner, TimeProvider clock)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SkewedScope(inner.CreateScope(), clock);
    }

    /// <summary>Also <see cref="IAsyncDisposable"/>, because the purge's scope holds a <c>DbContext</c>.</summary>
    private sealed class SkewedScope : IServiceScope, IAsyncDisposable
    {
        private readonly IServiceScope _inner;

        public SkewedScope(IServiceScope inner, TimeProvider clock)
        {
            _inner = inner;
            ServiceProvider = new SkewedProvider(inner.ServiceProvider, clock);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => _inner.Dispose();

        public async ValueTask DisposeAsync()
        {
            if (_inner is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
                return;
            }

            _inner.Dispose();
        }
    }

    /// <summary>Not <c>ISupportRequiredService</c>, which <c>GetRequiredService</c> does without.</summary>
    private sealed class SkewedProvider(IServiceProvider inner, TimeProvider clock) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(TimeProvider) ? clock : inner.GetService(serviceType);
    }

    /// <summary>Rewrites the marker under <paramref name="key"/> with its own <c>CommittedAt</c> (ADR-041).</summary>
    public Task ReplaceIdempotencyMarkerAsync(string key) =>
        ExecuteAsync(
            """
            DECLARE @committedAt datetimeoffset(7);

            SELECT @committedAt = CommittedAt
            FROM shipping.IdempotencyMarkers
            WHERE [Key] = {0};

            DELETE FROM shipping.IdempotencyMarkers WHERE [Key] = {0};

            INSERT INTO shipping.IdempotencyMarkers ([Key], CommittedAt)
            VALUES ({0}, @committedAt);
            """,
            key);

    /// <summary>The <c>rowversion</c> the purge identifies one marker by, or null if it is gone.</summary>
    public Task<byte[]?> IdempotencyMarkerVersionAsync(string key) =>
        ScalarAsync<byte[]?>(
            "SELECT Value = RowVersion FROM shipping.IdempotencyMarkers WHERE [Key] = {0}",
            key);

    /// <summary>Markers §8.5 holds for one key.</summary>
    public Task<int> IdempotencyMarkerCountAsync(string key) =>
        ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.IdempotencyMarkers WHERE [Key] = {0}",
            key);

    /// <summary>Rows the transaction probe holds for one id.</summary>
    public Task<int> ProbeRowCountAsync(Guid id) =>
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM shipping.TransactionProbe WHERE Id = {0}", id);

    public async ValueTask DisposeAsync()
    {
        // Each teardown runs even when an earlier one throws, so no container outlives a failed disposal.
        try
        {
            Factory?.Dispose();
        }
        finally
        {
            try
            {
                try
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
                finally
                {
                    await _sql.DisposeAsync();
                }
            }
            finally
            {
                // Null when the builder chain threw before the assignment.
                if (_rabbit is not null)
                    await _rabbit.DisposeAsync();
            }
        }
    }
}
