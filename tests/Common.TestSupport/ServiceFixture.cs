using System.Data.Common;
using System.Text.Json;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Respawn;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;

namespace Common.TestSupport;

/// <summary>A real SQL Server migrated by the real migrator, a real broker and, where asked, Redis (§12.4).</summary>
/// <remarks>
/// Each service's <c>ServiceFixture</c> derives from this and keeps its names, migrator and stubs (ADR-056). The
/// two §7.1 identities share the container's <c>sa</c> login but keep separate keys.
/// </remarks>
public abstract class ServiceFixture<TFactory, TEntryPoint, TDbContext> : IAsyncLifetime
    where TFactory : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
    where TDbContext : DbContext
{
    /// <summary>SQL Server's deadlock-victim error, the one fault <see cref="ResetAsync"/> retries.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>Attempts, not retries.</summary>
    private const int ResetAttempts = 3;

    private readonly string _service;
    private readonly string _schema;
    private readonly bool _schedules;

    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    /// <summary>§8.1's two servers, so a coordination key written to the evicting one is caught (§12.4).</summary>
    private readonly RedisContainer? _redisCache;

    private readonly RedisContainer? _redisCoordination;

    // Built in InitializeAsync, because BrokerContextPath, which it needs, can throw.
    private RabbitMqContainer? _rabbit;

    private Respawner? _respawner;

    /// <param name="service">The service's name, which names its database, schema and broker account.</param>
    /// <param name="redis">Whether the host claims keys on real Redis (§8.5), not the factory's default.</param>
    /// <param name="schedules">Whether it schedules, which takes §14.1's image for ADR-021's delayed exchange.</param>
    protected ServiceFixture(string service, bool redis = false, bool schedules = false)
    {
        _service = service;
        _schema = service.ToLowerInvariant();
        _schedules = schedules;

        if (!redis)
            return;

        _redisCache = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithCommand("--maxmemory-policy", "allkeys-lru")
            .Build();

        _redisCoordination = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithCommand("--maxmemory-policy", "noeviction")
            .Build();
    }

    /// <summary>The service's own database (§7.1), not the container's <c>master</c>.</summary>
    public string ConnectionString { get; private set; } = null!;

    public TFactory Factory { get; private set; } = null!;

    /// <summary>The exit code of the first real migration run.</summary>
    public int FirstRunExitCode { get; private set; } = -1;

    protected string BrokerConnectionString => _rabbit!.GetConnectionString();

    protected string? RedisCacheConnectionString => _redisCache?.GetConnectionString();

    protected string? RedisCoordinationConnectionString => _redisCoordination?.GetConnectionString();

    /// <summary>Runs the service's real §7.4 job host against <paramref name="connectionString"/>.</summary>
    protected abstract Task<int> MigrateAsync(string connectionString);

    protected abstract TFactory CreateFactory();

    /// <summary>The write grant the harness needs past ADR-036's, or null where the imported one serves.</summary>
    protected virtual string? HarnessWrite(string granted) => null;

    protected virtual Task StartStubsAsync() => Task.CompletedTask;

    protected virtual void ResetStubs()
    {
    }

    protected virtual ValueTask DisposeStubsAsync() => ValueTask.CompletedTask;

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

    /// <summary>Grants <see cref="HarnessWrite"/>'s write, keeping the imported configure and read.</summary>
    private async Task WidenWriteForTheHarnessAsync()
    {
        string user = $"{_schema}-svc";
        (string configure, string granted, string read) = ImportedGrant();

        string? write = HarnessWrite(granted);
        if (write is null)
            return;

        ExecResult result = await _rabbit!.ExecAsync(
            ["rabbitmqctl", "set_permissions", "-p", "/", user, configure, write, read],
            TestContext.Current.CancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not widen {user}'s broker permissions for the harness "
                + $"(exit {result.ExitCode}). stdout: {result.Stdout} stderr: {result.Stderr}");
        }

        // The build context's copy, the text the broker imported.
        (string Configure, string Write, string Read) ImportedGrant()
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

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync()
    {
        // The service's own account (ADR-036); the password is §14.1's local-development default.
        RabbitMqBuilder broker = new RabbitMqBuilder()
            .WithUsername($"{_schema}-svc")
            .WithPassword($"local-dev-{_schema}");

        if (_schedules)
        {
            // §14.1's broker Dockerfile, kept by WithCleanUp(false) so the plugin downloads once per machine.
            IFutureDockerImage image = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(BrokerContextPath())
                .WithDockerfile("Dockerfile")
                // A name of this service's own: Testcontainers writes the build context to a file named after
                // the image, and two suites building one name at once race on that file.
                .WithName($"ashamray-test-broker-{_schema}:4.1-delayed")
                .WithCleanUp(false)
                .Build();

            // Assigned before the image builds, so a failed build leaves a container for the teardown.
            _rabbit = broker.WithImage(image).Build();
            await image.CreateAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            // §14.1's configuration on the stock image, ADR-036's route for a service that does not schedule.
            _rabbit = broker
                .WithImage("rabbitmq:4.1-management-alpine")
                // A second copy of the Dockerfile's COPY targets, which check_permissions.py holds to it (ADR-036).
                .WithResourceMapping(
                    new FileInfo(Path.Combine(BrokerContextPath(), "definitions.json")),
                    "/etc/rabbitmq/")
                .WithResourceMapping(
                    new FileInfo(Path.Combine(BrokerContextPath(), "20-commerce.conf")),
                    "/etc/rabbitmq/conf.d/")
                .Build();
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        Task[] starting =
        [
            _sql.StartAsync(ct),
            _rabbit.StartAsync(ct),
            _redisCache?.StartAsync(ct) ?? Task.CompletedTask,
            _redisCoordination?.StartAsync(ct) ?? Task.CompletedTask
        ];
        await Task.WhenAll(starting);

        await WidenWriteForTheHarnessAsync();
        await StartStubsAsync();

        // The container hands out master; the service owns a database of its own (§7.1), which MigrateAsync creates.
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = _service;
        ConnectionString = connection.ConnectionString;

        FirstRunExitCode = await MigrateAsync(ConnectionString);

        Factory = CreateFactory();

        // A table of the test, not a migration, so it never ships to production.
        await ExecuteAsync(
            $"""
            CREATE TABLE {_schema}.TransactionProbe
            (
                Id   uniqueidentifier NOT NULL PRIMARY KEY,
                Note nvarchar(100)    NOT NULL
            );
            """);
    }

    /// <summary>§12.4's reset: truncates the service's schema, then resets its stubs.</summary>
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
                SchemasToInclude = [_schema]
            });

        // Bounded and small: a reset that keeps losing is not this race and should be seen.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await _respawner.ResetAsync(connection);
                break;
            }
            catch (SqlException e) when (e.Number == DeadlockVictim && attempt < ResetAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100 * attempt),
                    TestContext.Current.CancellationToken);
            }
        }

        ResetStubs();
    }

    /// <summary>Runs a statement outside any unit of work; a <c>{0}</c> placeholder is a SQL parameter.</summary>
    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        await db.Database.ExecuteSqlRawAsync(sql, parameters, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads one scalar outside any unit of work; a <c>{0}</c> placeholder is a SQL parameter.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        return await db.Database
            .SqlQueryRaw<T>(sql, parameters)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The migrations EF considers applied, asked through EF rather than its history table.</summary>
    public async Task<string[]> AppliedMigrationsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        return [.. await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)];
    }

    /// <summary>The host's own map (§9.4), which a service's <c>OutboxRows</c> stages through.</summary>
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
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        return await db.Set<OutboxMessage>()
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes outbox rows directly, past the staging.</summary>
    public async Task StageOutboxAsync(params OutboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        db.Set<OutboxMessage>().AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds a prior attempt count through the same column the dispatcher writes.</summary>
    public Task SetOutboxAttemptsAsync(Guid messageId, int attempts) =>
        ExecuteAsync(
            $"UPDATE {_schema}.OutboxMessages SET Attempts = {{0}} WHERE MessageId = {{1}};",
            attempts,
            messageId);

    /// <summary>Repoints a row's lane through SQL.</summary>
    public Task SetOutboxLaneAsync(Guid messageId, OutboxLane lane) =>
        ExecuteAsync(
            $"UPDATE {_schema}.OutboxMessages SET Lane = {{0}} WHERE MessageId = {{1}};",
            lane.ToString(),
            messageId);

    /// <summary>Clears retry backoff leases, so the next pass is gated only by the attempt cap.</summary>
    public Task ExpireOutboxLeasesAsync() =>
        ExecuteAsync($"UPDATE {_schema}.OutboxMessages SET LockedUntil = NULL WHERE ProcessedAt IS NULL;");

    /// <summary>Every inbox row, untracked, for asserting over (§9.5).</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        return await db.Set<InboxMessage>()
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The inbox rows one message wrote, untracked (§9.5), so other tests' rows are no part of it.</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync(Guid messageId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        return await db.Set<InboxMessage>()
            .AsNoTracking()
            .Where(m => m.MessageId == messageId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Every idempotency marker, untracked, for asserting over (§8.5).</summary>
    public async Task<IReadOnlyList<IdempotencyMarker>> IdempotencyMarkersAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        return await db.Set<IdempotencyMarker>()
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes inbox rows directly, past the filter.</summary>
    public async Task StageInboxAsync(params InboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        db.Set<InboxMessage>().AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes idempotency markers directly, for tests about the purge rather than §8.5.</summary>
    public async Task StageIdempotencyMarkersAsync(params IdempotencyMarker[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        db.Set<IdempotencyMarker>().AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>§8.5's registered claim store, the one ADR-039's purge asks.</summary>
    public IIdempotencyStore IdempotencyClaims =>
        Factory.Services.GetRequiredService<IIdempotencyStore>();

    /// <summary>Marks an outbox row processed at <paramref name="processedAt"/>.</summary>
    public Task SetOutboxProcessedAtAsync(Guid messageId, DateTimeOffset processedAt) =>
        ExecuteAsync(
            $"UPDATE {_schema}.OutboxMessages SET ProcessedAt = {{0}} WHERE MessageId = {{1}};",
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

    /// <summary>The same pass with the claim store substituted, so a test acts between select and delete.</summary>
    /// <remarks>The pass calls <see cref="IIdempotencyStore.UnheldAsync"/> between the two (ADR-039).</remarks>
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
            $$"""
            DECLARE @committedAt datetimeoffset(7);

            SELECT @committedAt = CommittedAt
            FROM {{_schema}}.IdempotencyMarkers
            WHERE [Key] = {0};

            DELETE FROM {{_schema}}.IdempotencyMarkers WHERE [Key] = {0};

            INSERT INTO {{_schema}}.IdempotencyMarkers ([Key], CommittedAt)
            VALUES ({0}, @committedAt);
            """,
            key);

    /// <summary>The <c>rowversion</c> the purge identifies one marker by.</summary>
    public Task<byte[]?> IdempotencyMarkerVersionAsync(string key) =>
        ScalarAsync<byte[]?>(
            $"SELECT Value = RowVersion FROM {_schema}.IdempotencyMarkers WHERE [Key] = {{0}}",
            key);

    /// <summary>Markers §8.5 holds for one key.</summary>
    public Task<int> IdempotencyMarkerCountAsync(string key) =>
        ScalarAsync<int>(
            $"SELECT Value = COUNT(*) FROM {_schema}.IdempotencyMarkers WHERE [Key] = {{0}}",
            key);

    /// <summary>Rows the transaction probe holds for one id.</summary>
    public Task<int> ProbeRowCountAsync(Guid id) =>
        ScalarAsync<int>($"SELECT Value = COUNT(*) FROM {_schema}.TransactionProbe WHERE Id = {{0}}", id);

    /// <summary>One <c>rabbitmqctl</c> listing, split into its tab-separated columns.</summary>
    protected async Task<IReadOnlyList<string[]>> BrokerRowsAsync(string[] listing)
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

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        // Each teardown runs even when an earlier one throws, so no container outlives a failed disposal.
        try
        {
            Factory?.Dispose();
        }
        finally
        {
            try
            {
                await DisposeStubsAsync();
            }
            finally
            {
                try
                {
                    await _sql.DisposeAsync();
                }
                finally
                {
                    try
                    {
                        // Null when BrokerContextPath() or the builder chain threw before the assignment.
                        if (_rabbit is not null)
                            await _rabbit.DisposeAsync();
                    }
                    finally
                    {
                        try
                        {
                            if (_redisCache is not null)
                                await _redisCache.DisposeAsync();
                        }
                        finally
                        {
                            if (_redisCoordination is not null)
                                await _redisCoordination.DisposeAsync();
                        }
                    }
                }
            }
        }
    }
}
