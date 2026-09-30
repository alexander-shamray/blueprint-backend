using System.Data.Common;
using System.Text.Json;
using Payments.Infrastructure.Persistence;
using Payments.Migrator;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Respawn;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using WireMock.Server;
using WireMock.Settings;
using Xunit;

namespace Payments.TestSupport;

/// <summary>A real SQL Server migrated by the real migrator, a real broker and the simulator (§12.4).</summary>
public sealed class ServiceFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    // Assigned in InitializeAsync, because the image carrying ADR-021's delayed exchange is built first.
    private RabbitMqContainer? _rabbit;

    private Respawner? _respawner;

    /// <summary>Widens <c>payments-svc</c>'s write to publish Ordering's events, which ADR-036 refuses.</summary>
    private async Task WidenWriteForTheHarnessAsync()
    {
        const string user = "payments-svc";
        const string write = "^(payments-|Common\\.Contracts|Payments\\.Infrastructure\\.Messaging:|MassTransit:)";

        (string configure, string read) = ImportedGrant();

        ExecResult result = await _rabbit!.ExecAsync(
            ["rabbitmqctl", "set_permissions", "-p", "/", user, configure, write, read],
            TestContext.Current.CancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not widen {user}'s broker permissions for the harness "
                + $"(exit {result.ExitCode}). stdout: {result.Stdout} stderr: {result.Stderr}");
        }

        static (string Configure, string Read) ImportedGrant()
        {
            string path = Path.Combine(BrokerContextPath(), "definitions.json");
            using JsonDocument definitions = JsonDocument.Parse(File.ReadAllText(path));

            foreach (JsonElement entry in definitions.RootElement.GetProperty("permissions").EnumerateArray())
            {
                if (entry.GetProperty("user").GetString() != user || entry.GetProperty("vhost").GetString() != "/")
                    continue;

                return (entry.GetProperty("configure").GetString()!, entry.GetProperty("read").GetString()!);
            }

            throw new InvalidOperationException(
                $"{path} grants {user} nothing on the default vhost, so there is no scope to preserve.");
        }
    }

    /// <summary>Payments's own database (§7.1), not the container's <c>master</c>.</summary>
    public string ConnectionString { get; private set; } = null!;

    public PaymentsApiFactory Factory { get; private set; } = null!;

    /// <summary>The provider, serving the simulator's mappings (§14.1); a test counts charges by its log.</summary>
    public WireMockServer Provider { get; private set; } = null!;

    /// <summary>The host's record of the order, observed.</summary>
    public ObservedOrderStore Orders => Factory.Orders;

    /// <summary>Fails the host's next commit with an outbox row staged, once; disposing the fault disarms it.</summary>
    public CommitFault FailNextCommit() => Factory.CommitFaults.Arm();

    /// <summary>Holds the next authorisation open after the provider answers; disposing the gate releases it.</summary>
    public ProviderGate PauseNextAuthorisation() => Factory.ProviderGates.PauseNextAuthorisation();

    /// <summary>Messages a queue holds, read from the broker, or zero when the queue does not exist yet.</summary>
    public async Task<int> QueueDepthAsync(string queue)
    {
        ExecResult result = await _rabbit!.ExecAsync(
            ["rabbitmqctl", "list_queues", "--quiet", "--no-table-headers", "name", "messages"],
            TestContext.Current.CancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not list the broker's queues (exit {result.ExitCode}). stderr: {result.Stderr}");
        }

        foreach (string line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = line.Split('\t', StringSplitOptions.TrimEntries);
            if (columns.Length == 2 && columns[0] == queue)
                return int.Parse(columns[1], System.Globalization.CultureInfo.InvariantCulture);
        }

        return 0;
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
        // A name of this fixture's own: Testcontainers writes the build context to a file named after
        // the image, and two suites building one name at once race on that file.
        IFutureDockerImage broker = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(BrokerContextPath())
            .WithDockerfile("Dockerfile")
            .WithName("ashamray-test-broker-payments:4.1-delayed")
            .WithCleanUp(false)
            .Build();

        // The password is §14.1's local-development default.
        _rabbit = new RabbitMqBuilder()
            .WithImage(broker)
            .WithUsername("payments-svc")
            .WithPassword("local-dev-payments")
            .Build();

        await broker.CreateAsync(TestContext.Current.CancellationToken);

        // Loopback, not every interface, which a workstation firewall stops to ask about.
        Provider = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
        Provider.ReadStaticMappings(SimulatorMappings.Directory());

        await Task.WhenAll(
            _sql.StartAsync(TestContext.Current.CancellationToken),
            _rabbit.StartAsync(TestContext.Current.CancellationToken));

        await WidenWriteForTheHarnessAsync();

        // The container hands out master; Payments owns a database of its own (§7.1), which MigrateAsync creates.
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = "Payments";
        ConnectionString = connection.ConnectionString;

        FirstRunExitCode = await RunMigratorAsync(ConnectionString);

        Factory = new PaymentsApiFactory(ConnectionString, _rabbit.GetConnectionString(), Provider.Urls[0] + "/");

        // A table of the test, not a migration, so it never ships to production.
        await ExecuteAsync(
            """
            CREATE TABLE payments.TransactionProbe
            (
                Id   uniqueidentifier NOT NULL PRIMARY KEY,
                Note nvarchar(100)    NOT NULL
            );
            """);
    }

    /// <summary>§12.4's reset: truncates the <c>payments</c> schema and restores the simulator's mappings.</summary>
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
                SchemasToInclude = ["payments"]
            });

        await _respawner.ResetAsync(connection);

        // The mappings as well as the log, because a stub a test adds outlives a log reset.
        Provider.ResetMappings();
        Provider.ReadStaticMappings(SimulatorMappings.Directory());
        Provider.ResetScenarios();
        Provider.ResetLogEntries();
    }

    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static async Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null)
    {
        string[] args =
        [
            .. Setting("ConnectionStrings:PaymentsMigrator", migratorConnectionString),
            .. Setting("ConnectionStrings:Payments", runtimeConnectionString)
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
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await db.Database.ExecuteSqlRawAsync(sql, parameters, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads one scalar outside any unit of work; a <c>{0}</c> placeholder is a SQL parameter.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await db.Database
            .SqlQueryRaw<T>(sql, parameters)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The migrations EF considers applied, asked through EF so the history table's location is not.</summary>
    public async Task<string[]> AppliedMigrationsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return [.. await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)];
    }

    /// <summary>The host's own map (§9.4), which <see cref="Outbox.OutboxRows"/> stages through.</summary>
    public MessageTypeMap MessageTypes =>
        Factory.Services.GetRequiredService<MessageTypeMap>();

    /// <summary>The host's payload format, so a staged row is written the way the dispatcher reads it.</summary>
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
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes rows directly, for tests about the dispatcher rather than the staging.</summary>
    public async Task StageOutboxAsync(params OutboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        db.OutboxMessages.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds a prior attempt count through the same column the dispatcher writes.</summary>
    public Task SetOutboxAttemptsAsync(Guid messageId, int attempts) =>
        ExecuteAsync(
            "UPDATE payments.OutboxMessages SET Attempts = {0} WHERE MessageId = {1};",
            attempts,
            messageId);

    /// <summary>Repoints a row's lane through SQL, making a row <see cref="OutboxMessage.Stage"/> refuses.</summary>
    public Task SetOutboxLaneAsync(Guid messageId, OutboxLane lane) =>
        ExecuteAsync(
            "UPDATE payments.OutboxMessages SET Lane = {0} WHERE MessageId = {1};",
            lane.ToString(),
            messageId);

    /// <summary>Clears retry backoff leases, so the next pass is gated only by the attempt cap.</summary>
    public Task ExpireOutboxLeasesAsync() =>
        ExecuteAsync("UPDATE payments.OutboxMessages SET LockedUntil = NULL WHERE ProcessedAt IS NULL;");

    /// <summary>Every inbox row, untracked, for asserting over (§9.5).</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await db.InboxMessages
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The inbox rows one message wrote, untracked (§9.5), so other tests' rows are no part of it.</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync(Guid messageId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await db.InboxMessages
            .AsNoTracking()
            .Where(m => m.MessageId == messageId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Every idempotency marker, untracked, for asserting over (§8.5).</summary>
    public async Task<IReadOnlyList<IdempotencyMarker>> IdempotencyMarkersAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await db.IdempotencyMarkers
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes inbox rows directly, for tests about the purge rather than the filter.</summary>
    public async Task StageInboxAsync(params InboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        db.InboxMessages.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes idempotency markers directly, for tests about the purge rather than §8.5.</summary>
    public async Task StageIdempotencyMarkersAsync(params IdempotencyMarker[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        db.IdempotencyMarkers.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The registered claim store, which never holds a key (ADR-039).</summary>
    public IIdempotencyStore IdempotencyClaims =>
        Factory.Services.GetRequiredService<IIdempotencyStore>();

    /// <summary>Ages a processed outbox row, so a retention test reaches the window without a fake clock.</summary>
    public Task SetOutboxProcessedAtAsync(Guid messageId, DateTimeOffset processedAt) =>
        ExecuteAsync(
            "UPDATE payments.OutboxMessages SET ProcessedAt = {0} WHERE MessageId = {1};",
            processedAt,
            messageId);

    /// <summary>Runs exactly one retention pass over every table. No timers, no waiting.</summary>
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
            FROM payments.IdempotencyMarkers
            WHERE [Key] = {0};

            DELETE FROM payments.IdempotencyMarkers WHERE [Key] = {0};

            INSERT INTO payments.IdempotencyMarkers ([Key], CommittedAt)
            VALUES ({0}, @committedAt);
            """,
            key);

    /// <summary>The <c>rowversion</c> the purge identifies one marker by, or null if it is gone.</summary>
    public Task<byte[]?> IdempotencyMarkerVersionAsync(string key) =>
        ScalarAsync<byte[]?>(
            "SELECT Value = RowVersion FROM payments.IdempotencyMarkers WHERE [Key] = {0}",
            key);

    /// <summary>Markers §8.5 holds for one key.</summary>
    public Task<int> IdempotencyMarkerCountAsync(string key) =>
        ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM payments.IdempotencyMarkers WHERE [Key] = {0}",
            key);

    /// <summary>Rows the transaction probe holds for one id.</summary>
    public Task<int> ProbeRowCountAsync(Guid id) =>
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.TransactionProbe WHERE Id = {0}", id);

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
                    Provider?.Stop();
                }
                finally
                {
                    await _sql.DisposeAsync();
                }
            }
            finally
            {
                // Null when the image build or the builder chain threw before the assignment.
                if (_rabbit is not null)
                    await _rabbit.DisposeAsync();
            }
        }
    }
}
