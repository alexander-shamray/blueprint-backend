using System.Data.Common;
using System.Text.Json;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Persistence;
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
// MassTransit names a Response as well, for IBus; the carrier's builder is
// the one this file means.
using Response = WireMock.ResponseBuilders.Response;

namespace Shipping.TestSupport;

/// <summary>
/// A real SQL Server migrated by the real migrator and a real RabbitMQ
/// (ADR-010, §12.4) — each the image §14.1's Compose file runs, so a test and a developer machine cannot disagree
/// about the engine. §4.1's home: the suites it serves cannot reference each
/// other.
/// </summary>
/// <remarks>§7.1's two database identities collapse here — the container's
/// <c>sa</c> holds DML and DDL — but not its two configuration keys, which
/// stay distinct so a migrator reading the wrong one is caught.</remarks>
public sealed class ServiceFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    // Built in InitializeAsync rather than here: its resource mappings
    // resolve through BrokerContextPath, which walks up to the solution root
    // and throws when the broker context is missing.
    private RabbitMqContainer? _rabbit;

    private Respawner? _respawner;

    /// <summary>
    /// Widens <c>shipping-svc</c>'s <c>write</c> for the suite, since these
    /// tests publish <c>OrderConfirmed</c> and <c>OrderCancelled</c> as
    /// <c>shipping-svc</c> onto Ordering's exchanges and ADR-036's production
    /// grant correctly refuses that. Widened here rather than in
    /// <c>definitions.json</c>, the deployed artefact a gate holds to the
    /// code. All three are read back from that file, and <c>write</c> gains
    /// Ordering's exchanges alone, so a route this service may not declare or
    /// publish to still fails here.
    /// </summary>
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

        // A silent failure here is the worst outcome available: every
        // consumer test would then fail on a publish, thirty seconds later,
        // naming a message rather than a permission.
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

    /// <summary>
    /// The connection each §7.1 identity would hold, pointed at Shipping's own
    /// database rather than the container's <c>master</c>.
    /// </summary>
    public string ConnectionString { get; private set; } = null!;

    public ShippingWorkerFactory Factory { get; private set; } = null!;

    /// <summary>
    /// The carrier, in process over the same mappings directory Compose
    /// mounts (spec, section 9), so no test double stands between the adapter
    /// and a real HTTP hop.
    /// </summary>
    public WireMockServer Carrier { get; private set; } = null!;

    /// <summary>
    /// ADR-052's owner, a real gRPC server on loopback: what a test queues here
    /// is what the worker's address read meets.
    /// </summary>
    public StubOrdering Ordering { get; } = new();

    /// <summary>
    /// Fails the host's next commit that moves a shipment, once. Disposing the
    /// returned fault disarms it.
    /// </summary>
    public CommitFault FailNextCommit() => Factory.CommitFaults.Arm();

    /// <summary>Every line the host has logged since the last reset.</summary>
    public CapturedLogs CapturedLogs => Factory.CapturedLogs;

    /// <summary>Runs exactly one fulfilment pass. No timers, no waiting.</summary>
    public Task<int> RunFulfilmentPassAsync() =>
        Factory.Services
            .GetRequiredService<FulfilmentWorker>()
            .RunOnceAsync(TestContext.Current.CancellationToken);

    /// <summary>Runs exactly one tracking pass. No timers, no waiting.</summary>
    public Task<int> RunTrackingPassAsync() =>
        Factory.Services.GetRequiredService<TrackingWorker>().ProcessBatchAsync(
            TestContext.Current.CancellationToken);

    /// <summary>
    /// A shipment the fulfilment worker booked: the order confirmed through the
    /// real broker, its address answered by <see cref="Ordering"/>, and one
    /// <see cref="RunFulfilmentPassAsync"/>, so no suite books a row by hand.
    /// The address is plain ASCII unless a caller names the two script parts.
    /// </summary>
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

        if (await RunFulfilmentPassAsync() != 1)
        {
            throw new InvalidOperationException(
                $"The fulfilment pass did not book the shipment for postal code {postalCode}.");
        }

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IShipmentRepository>()
            .GetByOrderAsync(new OrderId(order), TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"Order {order} was booked and its shipment is now absent.");
    }

    public Task<string> StatusAsync(ShipmentId id) =>
        ScalarAsync<string>("SELECT Value = Status FROM shipping.Shipments WHERE Id = {0}", id.Value);

    /// <summary>Null once the shipment is terminal and has nothing further to learn.</summary>
    public Task<DateTimeOffset?> NextPollAtAsync(ShipmentId id) =>
        ScalarAsync<DateTimeOffset?>("SELECT Value = NextPollAt FROM shipping.Shipments WHERE Id = {0}", id.Value);

    public Task<int> AttemptsAsync(ShipmentId id) =>
        ScalarAsync<int>("SELECT Value = Attempts FROM shipping.Shipments WHERE Id = {0}", id.Value);

    /// <summary>Null once a pass has released the row it claimed.</summary>
    public Task<DateTimeOffset?> LockedUntilAsync(ShipmentId id) =>
        ScalarAsync<DateTimeOffset?>("SELECT Value = LockedUntil FROM shipping.Shipments WHERE Id = {0}", id.Value);

    /// <summary>
    /// The engine's own clock, for a comparison with a column the engine
    /// stamped: the container's clock and the host's can disagree.
    /// </summary>
    public Task<DateTimeOffset> DatabaseNowAsync() =>
        ScalarAsync<DateTimeOffset>("SELECT Value = SYSDATETIMEOFFSET()");

    /// <summary>
    /// Repoints a booked row at a reference a test's own carrier answers, so
    /// the row is the real one and only the carrier's answer is scripted.
    /// </summary>
    public Task SetCarrierReferenceAsync(ShipmentId id, string reference) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET CarrierReference = {0} WHERE Id = {1};",
            reference,
            id.Value);

    /// <summary>
    /// Stamps a live tracking lease on every pollable row, so a second pass
    /// meets a row in flight rather than one a previous pass has finished with.
    /// </summary>
    public Task ClaimForTrackingAsync() =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET LockedUntil = DATEADD(second, {0}, SYSDATETIMEOFFSET()) " +
            "WHERE Status IN ('Booked', 'Dispatched');",
            TrackingWorker.LeaseSeconds);

    /// <summary>
    /// The same, under the fulfilment worker's lease and over its populations.
    /// Two helpers rather than one with a parameter: what a test is saying is
    /// WHICH worker holds the row, and a number passed in says that nowhere.
    /// </summary>
    public Task ClaimForFulfilmentAsync() =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET LockedUntil = DATEADD(second, {0}, SYSDATETIMEOFFSET()) " +
            "WHERE Status = 'Pending' " +
            "   OR (Status = 'Booked' AND CancellationRequestedAt IS NOT NULL AND CancellationRefusedAt IS NULL);",
            FulfilmentWorker.LeaseSeconds);

    /// <summary>Lets every lease lapse, which is what a killed replica leaves behind.</summary>
    public Task ExpireLeasesAsync() =>
        ExecuteAsync("UPDATE shipping.Shipments SET LockedUntil = NULL;");

    /// <summary>
    /// Stamps the cancellation request without asking the carrier, which is the
    /// one state a row is due to both workers at once (spec, sections 5 and 6).
    /// </summary>
    public Task RequestCancellationAsync(ShipmentId id) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET CancellationRequestedAt = SYSDATETIMEOFFSET() WHERE Id = {0};",
            id.Value);

    /// <summary>
    /// How long a staged step may take — a delivery through the broker or a
    /// bus coming up. A deadline, not a sleep, so it costs nothing when the
    /// step is prompt.
    /// </summary>
    private static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Polls to <see cref="StepDeadline"/> and throws when it lapses, which is
    /// what stages a step on something another has already done.
    /// </summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
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

    /// <summary>
    /// The exchanges bound to one destination, read from the broker itself:
    /// a binding is declared when the endpoint starts, and only a real broker
    /// holds the result.
    /// </summary>
    public async Task<string[]> BindingsAsync(string queue) =>
    [
        .. (await BrokerRowsAsync(["list_bindings", "source_name", "destination_name"]))
            .Where(columns => columns.Length == 2 && columns[1] == queue)
            .Select(columns => columns[0])
    ];

    /// <summary>
    /// A second worker host over these containers and this Ordering stub,
    /// answered by a carrier the caller started. A resilience pipeline belongs
    /// to a host, so a suite whose cases fill the breaker takes one of these
    /// per test (<c>CarrierHop</c>).
    /// </summary>
    public ShippingWorkerFactory NewWorkerHost(string carrierBaseUrl) =>
        new(
            ConnectionString,
            _rabbit!.GetConnectionString(),
            carrierBaseUrl,
            addressSourceBaseUrl: Ordering.Address.ToString());

    /// <summary>Makes one carrier server answer one path with one status code, after an optional delay.</summary>
    /// <remarks>
    /// The handle disposes the mapping. A mapping on a running server rather
    /// than a file under deploy/compose/carrier-simulator: that directory is the
    /// postal-code script Compose and this fixture share (spec, section 9), and
    /// an answer nobody can reach from a checkout is no part of it. The server
    /// is a parameter rather than <see cref="Carrier"/>, because the suites that
    /// script an answer run a host of their own.
    /// </remarks>
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

    /// <summary>
    /// A carrier server on loopback rather than WireMock's default of every
    /// interface: a socket on 0.0.0.0 is what a workstation firewall stops to
    /// ask about, and the only caller is the in-process host under test.
    /// </summary>
    public static WireMockServer StartCarrier()
    {
        WireMockServer server = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
        server.ReadStaticMappings(SimulatorMappings.Directory());

        return server;
    }

    /// <summary>
    /// One <c>rabbitmqctl</c> listing, split into its tab-separated columns.
    /// </summary>
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

    /// <summary>
    /// Removes one mapping and leaves the rest. <c>ResetAsync</c> resets the
    /// whole server between tests and is the backstop; this is what keeps a
    /// mapping from outliving the assertion it was added for inside one.
    /// </summary>
    private sealed class CarrierMapping(WireMockServer server, Guid id) : IDisposable
    {
        public void Dispose() => server.DeleteMapping(id);
    }

    /// <summary>The exit code of the first real migration run.</summary>
    public int FirstRunExitCode { get; private set; } = -1;

    /// <summary>
    /// The directory holding §14.1's broker Dockerfile, found by walking up to
    /// <c>Platform.slnx</c>.
    /// </summary>
    /// <remarks>
    /// A second copy of Ordering.TestSupport's helper, deliberately. §4.3
    /// permits exactly one assembly to cross a service boundary and a test
    /// helper is not it — the same rule that gives the gateway suite its own
    /// <c>TestAuthHandler</c>.
    /// </remarks>
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
        // §14.1's broker configuration on the stock image, rather than
        // §14.1's built image. Shipping needs the per-service accounts and not
        // ADR-021's delayed-exchange plugin: it runs no saga and schedules
        // nothing, so the build would buy it only the one thing it cannot use.
        // Not building also keeps concurrent test hosts off one build
        // context — Testcontainers writes that context to a tar named after
        // the image, and separate processes instantiating this fixture would
        // race on that file. The mapped paths must match the Dockerfile's
        // COPY targets; `check_permissions.py` asserts they agree rather than
        // leaving it to a reader.
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

        // Together, §12.4's printed shape — the broker's start hides inside
        // SQL Server's, which is the slower of the two by some margin.
        await Task.WhenAll(
            _sql.StartAsync(TestContext.Current.CancellationToken),
            _rabbit.StartAsync(TestContext.Current.CancellationToken));

        await WidenWriteForTheHarnessAsync();

        Carrier = StartCarrier();
        await Ordering.InitializeAsync();

        // The container hands out a connection to master; Shipping owns a
        // database of its own (§7.1), and MigrateAsync is what creates it.
        // DbConnectionStringBuilder out of habit rather than necessity now:
        // this project does carry the provider package, for the open
        // SqlConnection Respawn inspects in ResetAsync.
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = "Shipping";
        ConnectionString = connection.ConnectionString;

        FirstRunExitCode = await RunMigratorAsync(ConnectionString);

        Factory = NewWorkerHost(Carrier.Urls[0] + "/");

        // A table for the transaction tests, created here and not in a
        // migration. It is a fixture of the test rather than a table of the
        // service, and putting it in a migration to make a test easier would
        // ship it to production.
        await ExecuteAsync(
            """
            CREATE TABLE shipping.TransactionProbe
            (
                Id   uniqueidentifier NOT NULL PRIMARY KEY,
                Note nvarchar(100)    NOT NULL
            );
            """);
    }

    /// <summary>
    /// §12.4's reset: truncation over the <c>shipping</c> schema, far faster
    /// than recreating it and honest where a rolled-back transaction would
    /// hide transaction-related bugs. Tests that share the collection call
    /// this from <c>InitializeAsync</c>; suites asserting the migrator or the
    /// probe table arrange per-test identities instead and never need it.
    /// </summary>
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

        // The mappings as well as the log: a stub a test adds outlives a log
        // reset, so re-reading the simulator's own mappings (spec, section 9)
        // is what leaves every test the same carrier to start from. The
        // Ordering stub and the captured log are the collection's the same
        // way: a status one test queued would answer the next test's first
        // read, and a line an earlier pass logged would be searched again.
        Carrier.ResetMappings();
        Carrier.ReadStaticMappings(SimulatorMappings.Directory());
        Carrier.ResetLogEntries();
        Ordering.Reset();
        CapturedLogs.Clear();
    }

    /// <summary>
    /// Drives the real §7.4 job host, so the smoke covers which connection
    /// string it reads and what it returns — not a copy of its wiring. A null
    /// argument leaves that key unset, which is how the §7.1 boundary is
    /// tested rather than assumed.
    /// </summary>
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

    /// <summary>
    /// Runs a statement outside any unit of work, for arranging. Placeholders
    /// are <c>{0}</c>-style and EF turns each into a real SQL parameter — the
    /// same rule <see cref="ScalarAsync{T}"/> states, and for the same two
    /// reasons: a formatted string here would be both an injection shape and
    /// a CA1305.
    /// </summary>
    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        await db.Database.ExecuteSqlRawAsync(sql, parameters, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads one scalar outside any unit of work, for asserting. Placeholders
    /// are <c>{0}</c>-style and EF turns each into a real SQL parameter — a
    /// formatted string here would be both an injection shape and a CA1305.
    /// </summary>
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

    /// <summary>
    /// The migrations EF considers applied. Asked through EF rather than by
    /// selecting from <c>__EFMigrationsHistory</c>, so the assertion is about
    /// what that table holds and not about where it lives — which is EF's to
    /// decide, is configured by <c>MigrationsHistoryTable</c> rather than by
    /// this context's <c>HasDefaultSchema</c>, and is no part of what this
    /// fixture claims.
    /// </summary>
    public async Task<string[]> AppliedMigrationsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        return [.. await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)];
    }

    /// <summary>
    /// The host's own map (§9.4), with this assembly's events in it — the
    /// builders in <see cref="Outbox.OutboxRows"/> stage through it, so a row
    /// a test writes is a row the running dispatcher can resolve.
    /// </summary>
    public MessageTypeMap MessageTypes =>
        Factory.Services.GetRequiredService<MessageTypeMap>();

    /// <summary>
    /// The host's payload format, converters included — so a row a test
    /// stages is written the way the dispatcher will read it.
    /// </summary>
    public OutboxJson OutboxJson =>
        Factory.Services.GetRequiredService<OutboxJson>();

    /// <summary>Runs exactly one claim-and-deliver pass. No timers, no waiting.</summary>
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

    /// <summary>
    /// Seeds a prior attempt count through the same column the dispatcher
    /// writes. Explicit rather than hidden in a builder, so no state carries
    /// between tests (§12.8).
    /// </summary>
    public Task SetOutboxAttemptsAsync(Guid messageId, int attempts) =>
        ExecuteAsync(
            "UPDATE shipping.OutboxMessages SET Attempts = {0} WHERE MessageId = {1};",
            attempts,
            messageId);

    /// <summary>
    /// Repoints a staged row at the other lane, which is the only way to
    /// produce the row <see cref="OutboxMessage.Stage"/> refuses: a lane that
    /// disagrees with its payload. Written through SQL on purpose — the point
    /// of the dispatcher's re-checks is rows that reached the table without
    /// passing the staging guards, and a test that could build one in process
    /// would be testing a different claim.
    /// </summary>
    public Task SetOutboxLaneAsync(Guid messageId, OutboxLane lane) =>
        ExecuteAsync(
            "UPDATE shipping.OutboxMessages SET Lane = {0} WHERE MessageId = {1};",
            lane.ToString(),
            messageId);

    /// <summary>
    /// Clears retry backoff leases so the next pass is gated only by the
    /// attempt cap. Lets a test distinguish "backed off" from "abandoned"
    /// without sleeping.
    /// </summary>
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

    /// <summary>
    /// The inbox rows one message wrote, untracked (§9.5) — the read an
    /// assertion about the filter wants, and the one
    /// <see cref="InboxAsync()"/> cannot be.
    /// </summary>
    /// <remarks>An unscoped read asserts both that the duplicate was
    /// suppressed and that no other row exists in the schema, and only the
    /// first is <c>InboxFilter&lt;T&gt;</c>'s guarantee: classes in
    /// <c>IntegrationCollection</c> share this fixture and run in sequence.
    /// <see cref="InboxAsync()"/> stays for the table itself.</remarks>
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

    /// <summary>
    /// Writes inbox rows directly, for tests about the purge rather than the
    /// filter. The filter's own tests go through a consume pipeline, because
    /// what they are about is which of <c>MessageId</c> and <c>Endpoint</c> the
    /// row is keyed on and when it is committed.
    /// </summary>
    public async Task StageInboxAsync(params InboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        db.InboxMessages.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Writes idempotency markers directly, for tests about the purge rather
    /// than about §8.5. The marker's own tests go through the pipeline, because
    /// what they are about is that the row commits with the work and vanishes
    /// with a rollback — which staging it here would assume rather than show.
    /// </summary>
    public async Task StageIdempotencyMarkersAsync(params IdempotencyMarker[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        db.IdempotencyMarkers.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// §8.5's claim store, so a retention test can put a live claim behind a
    /// staged marker and take it away again.
    /// </summary>
    /// <remarks>The registered store against the real container, rather than
    /// a double: ADR-039 makes the purge ask this store whether a claim is
    /// gone, so a test of that has to leave the store able to say no — and a
    /// substitute would be asserting the test's own idea of the answer
    /// against a pass that reads the real one.</remarks>
    public IIdempotencyStore IdempotencyClaims =>
        Factory.Services.GetRequiredService<IIdempotencyStore>();

    /// <summary>
    /// Ages a processed outbox row, which is how a retention test reaches the
    /// window without a fake clock: the purge resolves <c>TimeProvider</c> from
    /// its own scope inside the host, and moving a row backwards is both
    /// simpler and closer to what the table actually looks like.
    /// </summary>
    public Task SetOutboxProcessedAtAsync(Guid messageId, DateTimeOffset processedAt) =>
        ExecuteAsync(
            "UPDATE shipping.OutboxMessages SET ProcessedAt = {0} WHERE MessageId = {1};",
            processedAt,
            messageId);

    /// <summary>Runs exactly one retention pass over every table. No timers, no waiting.</summary>
    public Task<(int Outbox, int Inbox, int Idempotency)> PurgeRetentionAsync() =>
        Factory.Services
            .GetRequiredService<RetentionPurgeService>()
            .PurgeAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// One pass under a policy of the test's own, for the batching edges the
    /// registered one cannot show: a batch of 5,000 would need 10,001 rows
    /// before a second batch ran at all. Constructed rather than resolved,
    /// because the policy is a constructor argument and the service composes
    /// a statement per table from the same registered tables either way, so
    /// what varies is the batching and nothing else.
    /// </summary>
    public Task<(int Outbox, int Inbox, int Idempotency)> PurgeWithAsync(RetentionPolicy policy) =>
        PurgeWithAsync(policy, Factory.Services.GetRequiredService<IIdempotencyStore>());

    /// <summary>
    /// The same pass with the claim store substituted, which is the only seam
    /// in the marker's leg wide enough to reach the window the split opened.
    /// </summary>
    /// <remarks><c>UnheldAsync</c> is called between the <c>SELECT</c> and
    /// the <c>DELETE</c>, which is exactly where a replacement lands in
    /// production, so a decorator that mutates the table while answering puts
    /// a test on the far side of that window deterministically. What this
    /// overload substitutes is when the answer arrives, not what it says; the
    /// registered store stays the default above.</remarks>
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
    /// One pass under a policy of the test's own and a registered clock moved
    /// forward by <paramref name="skew"/>, because nothing else in this suite
    /// can tell the marker's cutoff from the other two.
    /// </summary>
    /// <remarks>The marker's cutoff is the server's — <c>DATEADD(second,
    /// -@WindowSeconds, SYSDATETIMEOFFSET())</c>, ADR-038 — and the other two
    /// the application's, so moving only the registered clock separates them.
    /// A wrapped <see cref="IServiceScopeFactory"/> rather than a second host,
    /// because the pass resolves <c>TimeProvider</c> from its scope.</remarks>
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

    /// <summary>
    /// The system clock plus a fixed offset, which is what a test skewing
    /// one end of a two-clock comparison needs. Hand-written rather than
    /// <c>FakeTimeProvider</c>: that package is pinned centrally, but this
    /// project does not reference it, and a frozen clock is not wanted
    /// here either — the pass compares against rows staged in real time,
    /// so the substitute has to keep running and simply run ahead.
    /// </summary>
    private sealed class SkewedClock(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + skew;
    }

    /// <summary>
    /// Hands out scopes whose <see cref="TimeProvider"/> is
    /// <see cref="SkewedClock"/> and whose every other service is the host's.
    /// </summary>
    private sealed class SkewedScopeFactory(IServiceScopeFactory inner, TimeProvider clock)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SkewedScope(inner.CreateScope(), clock);
    }

    /// <summary>
    /// A real scope wearing a substituted provider. <see cref="IAsyncDisposable"/>
    /// as well as <see cref="IDisposable"/>, because <c>AsyncServiceScope</c>
    /// asks for the first and silently falls back to the second — and the
    /// purge's own scope holds a <c>DbContext</c>, which is exactly the kind of
    /// service that owes its disposal an <c>await</c>.
    /// </summary>
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

    /// <summary>
    /// One service substituted and everything else delegated. Deliberately not
    /// <c>ISupportRequiredService</c>: <c>GetRequiredService</c> falls back to
    /// <see cref="GetService"/> when a provider does not implement it, so the
    /// one override is enough and there is no second lookup path to keep in
    /// step with this one.
    /// </summary>
    private sealed class SkewedProvider(IServiceProvider inner, TimeProvider clock) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(TimeProvider) ? clock : inner.GetService(serviceType);
    }

    /// <summary>
    /// Deletes the marker under <paramref name="key"/> and writes a fresh one
    /// back under the same key and the same <c>CommittedAt</c> — the ABA a
    /// purge pass can meet between its <c>SELECT</c> and its <c>DELETE</c>.
    /// </summary>
    /// <remarks>Preserving the timestamp is the whole of it: a replacement
    /// stamped at a fresh instant is a different row to any key that includes
    /// <c>CommittedAt</c>, so only an identical one stages the collision. The
    /// <c>rowversion</c> cannot be carried across, and that a replacement gets
    /// a new one is the property under test.</remarks>
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

    /// <summary>Markers §8.5 holds for one key — nought or one, and which is the point.</summary>
    public Task<int> IdempotencyMarkerCountAsync(string key) =>
        ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.IdempotencyMarkers WHERE [Key] = {0}",
            key);

    /// <summary>Rows the transaction probe holds for one id.</summary>
    public Task<int> ProbeRowCountAsync(Guid id) =>
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM shipping.TransactionProbe WHERE Id = {0}", id);

    public async ValueTask DisposeAsync()
    {
        // Each teardown runs even when an earlier one throws: a failed
        // factory or SQL disposal must not leave the broker container
        // running for the rest of the CI job.
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
                // Null-safe on Ordering's fixture's argument: the image build
                // and the builder chain both run before the field is assigned,
                // and either can throw.
                if (_rabbit is not null)
                    await _rabbit.DisposeAsync();
            }
        }
    }
}
