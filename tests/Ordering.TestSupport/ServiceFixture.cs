using System.Data.Common;
using System.Text.Json;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Infrastructure.Persistence;
using Ordering.Migrator;
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
using Testcontainers.Redis;
using Xunit;

namespace Ordering.TestSupport;

/// <summary>A real SQL Server migrated by the real migrator, a real broker and two Redis servers (§12.4).</summary>
/// <remarks>
/// The <c>sa</c> login holds both §7.1 identities, but the two configuration keys stay distinct so the
/// migrator can be caught reading the wrong one.
/// </remarks>
public sealed class ServiceFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    /// <summary>§8.1's two servers, so a coordination key written to the evicting one is caught (§12.4).</summary>
    private readonly RedisContainer _redisCache = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "allkeys-lru")
        .Build();

    private readonly RedisContainer _redisCoordination = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "noeviction")
        .Build();

    /// <summary>Built in <see cref="InitializeAsync"/>, beside the image carrying ADR-021's delayed exchange.</summary>
    private RabbitMqContainer? _rabbit;

    private Respawner? _respawner;

    /// <summary>SQL Server's deadlock-victim error, the one fault <see cref="ResetAsync"/> retries.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>Attempts, not retries.</summary>
    private const int ResetAttempts = 3;

    /// <summary>Ordering's own database (§7.1), not the container's <c>master</c>.</summary>
    public string ConnectionString { get; private set; } = null!;

    public OrderingApiFactory Factory { get; private set; } = null!;

    /// <summary>The exit code of the first real migration run.</summary>
    public int FirstRunExitCode { get; private set; } = -1;

    /// <summary>
    /// <c>deploy/compose/rabbitmq</c> beside <c>Platform.slnx</c> rather than <c>.git</c>, which a worktree
    /// stores as a file; it throws rather than falling back to a stock broker.
    /// </summary>
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

    /// <summary>Widens <c>ordering-svc</c>'s write to publish the saga's inbound events (ADR-036).</summary>
    private async Task WidenWriteForTheHarnessAsync()
    {
        const string user = "ordering-svc";
        const string write =
            "^(ordering-|inventory-commands|payments-commands|Common\\.Contracts|" +
            "Ordering\\.Infrastructure\\.Messaging:|MassTransit:)";

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

        // The mapped file, which is the text the broker imported.
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

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync()
    {
        // §14.1's broker Dockerfile, kept by WithCleanUp(false) so the plugin downloads once per machine.
        IFutureDockerImage broker = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(BrokerContextPath())
            .WithDockerfile("Dockerfile")
            // A name of this fixture's own: Testcontainers writes the build context to a file named after
            // the image, and two suites building one name at once race on that file.
            .WithName("ashamray-test-broker-ordering:4.1-delayed")
            .WithCleanUp(false)
            .Build();

        // Assigned before the image builds, so a failed build leaves a container for the teardown.
        // The service's own account (ADR-036); the password is §14.1's local-development default.
        _rabbit = new RabbitMqBuilder()
            .WithImage(broker)
            .WithUsername("ordering-svc")
            .WithPassword("local-dev-ordering")
            .Build();

        await broker.CreateAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(
            _sql.StartAsync(TestContext.Current.CancellationToken),
            _rabbit.StartAsync(TestContext.Current.CancellationToken),
            _redisCache.StartAsync(TestContext.Current.CancellationToken),
            _redisCoordination.StartAsync(TestContext.Current.CancellationToken));

        await WidenWriteForTheHarnessAsync();

        // The container hands out master; Ordering owns a database of its own (§7.1), which MigrateAsync creates.
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = "Ordering";
        ConnectionString = connection.ConnectionString;

        FirstRunExitCode = await RunMigratorAsync(ConnectionString);

        // Real Redis rather than the factory's unreachable default, because §8.5 claims a key per protected command.
        Factory = new OrderingApiFactory(
            ConnectionString,
            _rabbit.GetConnectionString(),
            _redisCache.GetConnectionString(),
            _redisCoordination.GetConnectionString());

        // A table of the test, not a migration, so it never ships to production.
        await ExecuteAsync(
            """
            CREATE TABLE ordering.TransactionProbe
            (
                Id   uniqueidentifier NOT NULL PRIMARY KEY,
                Note nvarchar(100)    NOT NULL
            );
            """);
    }

    /// <summary>§12.4's reset: truncates the <c>ordering</c> schema; saga timeouts stay armed (ADR-021).</summary>
    /// <remarks>
    /// Retries a deadlock between Respawn's delete and a saga transaction still committing, whose lock order
    /// opposes it (ADR-032).
    /// </remarks>
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
                SchemasToInclude = ["ordering"]
            });

        // Bounded and small: a reset that keeps losing is not this race and should be seen.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await _respawner.ResetAsync(connection);
                return;
            }
            catch (SqlException e) when (e.Number == DeadlockVictim && attempt < ResetAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100 * attempt),
                    TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static async Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null)
    {
        string[] args =
        [
            .. Setting("ConnectionStrings:OrderingMigrator", migratorConnectionString),
            .. Setting("ConnectionStrings:Ordering", runtimeConnectionString)
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
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        await db.Database.ExecuteSqlRawAsync(sql, parameters, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads one scalar outside any unit of work; a <c>{0}</c> placeholder is a SQL parameter.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.Database
            .SqlQueryRaw<T>(sql, parameters)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The migrations EF considers applied, asked through EF, which decides where their table lives.</summary>
    public async Task<string[]> AppliedMigrationsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

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

    /// <summary>
    /// Persists a real aggregate, so the row meets §5's invariants, with its events cleared so a seeded order
    /// stages no outbox row.
    /// </summary>
    public async Task<Guid> SeedOrderAsync(Guid customerId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        Order order = Order.Place(
            new CustomerId(customerId),
            Address.Of("1 Test Street", null, "Almaty", "050000", "KZ"),
            [(ProductId.New(), 1, Money.Of(19.99m, "EUR"))],
            "EUR",
            DateTimeOffset.UtcNow);
        order.ClearDomainEvents();

        db.Orders.Add(order);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return order.Id.Value;
    }

    /// <summary>Every outbox row, untracked, for asserting over.</summary>
    public async Task<IReadOnlyList<OutboxMessage>> OutboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes rows directly, for tests about the dispatcher rather than the staging.</summary>
    public async Task StageOutboxAsync(params OutboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        db.OutboxMessages.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds a prior attempt count through the same column the dispatcher writes.</summary>
    public Task SetOutboxAttemptsAsync(Guid messageId, int attempts) =>
        ExecuteAsync(
            "UPDATE ordering.OutboxMessages SET Attempts = {0} WHERE MessageId = {1};",
            attempts,
            messageId);

    /// <summary>Repoints a row's lane through SQL, making a row <see cref="OutboxMessage.Stage"/> refuses.</summary>
    public Task SetOutboxLaneAsync(Guid messageId, OutboxLane lane) =>
        ExecuteAsync(
            "UPDATE ordering.OutboxMessages SET Lane = {0} WHERE MessageId = {1};",
            lane.ToString(),
            messageId);

    /// <summary>Clears retry backoff leases, so the next pass is gated only by the attempt cap.</summary>
    public Task ExpireOutboxLeasesAsync() =>
        ExecuteAsync("UPDATE ordering.OutboxMessages SET LockedUntil = NULL WHERE ProcessedAt IS NULL;");

    /// <summary>Every inbox row, untracked, for asserting over (§9.5).</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.InboxMessages
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The inbox rows one message wrote, untracked (§9.5), so other tests' rows are no part of it.</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync(Guid messageId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.InboxMessages
            .AsNoTracking()
            .Where(m => m.MessageId == messageId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Every idempotency marker, untracked, for asserting over (§8.5).</summary>
    public async Task<IReadOnlyList<IdempotencyMarker>> IdempotencyMarkersAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.IdempotencyMarkers
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes inbox rows directly, for tests about the purge rather than the filter.</summary>
    public async Task StageInboxAsync(params InboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        db.InboxMessages.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes idempotency markers directly, for tests about the purge rather than §8.5.</summary>
    public async Task StageIdempotencyMarkersAsync(params IdempotencyMarker[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        db.IdempotencyMarkers.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The registered claim store, so a retention test can hold a claim behind a marker (ADR-039).</summary>
    public IIdempotencyStore IdempotencyClaims =>
        Factory.Services.GetRequiredService<IIdempotencyStore>();

    /// <summary>Ages a processed outbox row, so a retention test reaches the window without a fake clock.</summary>
    public Task SetOutboxProcessedAtAsync(Guid messageId, DateTimeOffset processedAt) =>
        ExecuteAsync(
            "UPDATE ordering.OutboxMessages SET ProcessedAt = {0} WHERE MessageId = {1};",
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

    /// <summary>
    /// The same pass with the claim store substituted, which the pass calls between its <c>SELECT</c> and its
    /// <c>DELETE</c> (ADR-039).
    /// </summary>
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

    /// <summary>Hands out scopes whose <see cref="TimeProvider"/> is <see cref="SkewedClock"/>.</summary>
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
            FROM ordering.IdempotencyMarkers
            WHERE [Key] = {0};

            DELETE FROM ordering.IdempotencyMarkers WHERE [Key] = {0};

            INSERT INTO ordering.IdempotencyMarkers ([Key], CommittedAt)
            VALUES ({0}, @committedAt);
            """,
            key);

    /// <summary>The <c>rowversion</c> the purge identifies one marker by, or null if it is gone.</summary>
    public Task<byte[]?> IdempotencyMarkerVersionAsync(string key) =>
        ScalarAsync<byte[]?>(
            "SELECT Value = RowVersion FROM ordering.IdempotencyMarkers WHERE [Key] = {0}",
            key);

    /// <summary>Markers §8.5 holds for one key.</summary>
    public Task<int> IdempotencyMarkerCountAsync(string key) =>
        ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.IdempotencyMarkers WHERE [Key] = {0}",
            key);

    /// <summary>Rows the transaction probe holds for one id.</summary>
    public Task<int> ProbeRowCountAsync(Guid id) =>
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM ordering.TransactionProbe WHERE Id = {0}", id);

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
                await _sql.DisposeAsync();
            }
            finally
            {
                // Null when BrokerContextPath() or the builder chain threw before the assignment.
                try
                {
                    if (_rabbit is not null)
                        await _rabbit.DisposeAsync();
                }
                finally
                {
                    // Field initialisers, so they need no null guard.
                    try
                    {
                        await _redisCache.DisposeAsync();
                    }
                    finally
                    {
                        await _redisCoordination.DisposeAsync();
                    }
                }
            }
        }
    }
}
