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
using Xunit;

namespace Payments.TestSupport;

/// <summary>
/// A real SQL Server, migrated by the real migrator (ADR-010, §12.4), a real
/// RabbitMQ on the image §14.1 builds, with ADR-021's delayed exchange, and
/// the simulator's mappings in process. Test projects cannot reference each
/// other (§4.1), so one that needs containers declares its own
/// <c>IntegrationCollection</c> over this type. Tests collapse §7.1's two
/// database identities — <c>sa</c> holds DML and DDL — but not the two
/// configuration keys, so the migrator can be caught reading the wrong one.
/// </summary>
public sealed class ServiceFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    // Assigned in InitializeAsync rather than here, because the image has to
    // be BUILT and a field initialiser cannot await. A stock broker takes the
    // delayed scheduler's registration and reports healthy, and the first
    // redelivery then hangs on a declare it refuses (ADR-021).
    private RabbitMqContainer? _rabbit;

    private Respawner? _respawner;

    /// <summary>
    /// Widens <c>payments-svc</c>'s <c>write</c> for the suite, since these
    /// tests publish <c>OrderPlaced</c> and <c>OrderCancelled</c> as
    /// <c>payments-svc</c> onto Ordering's exchanges and ADR-036's production
    /// grant correctly refuses that. Widened here rather than in
    /// <c>definitions.json</c>, the deployed artefact a gate holds to the
    /// code; <c>configure</c> and <c>read</c> are read back from that same
    /// file rather than restated, so a route this service may not declare
    /// still fails here.
    /// </summary>
    private async Task WidenWriteForTheHarnessAsync()
    {
        const string user = "payments-svc";
        const string write = "^(payments-|Common\\.Contracts|Payments\\.Infrastructure\\.Messaging:|MassTransit:)";

        (string configure, string read) = ImportedGrant();

        ExecResult result = await _rabbit!.ExecAsync(
            ["rabbitmqctl", "set_permissions", "-p", "/", user, configure, write, read],
            TestContext.Current.CancellationToken);

        // A silent failure here is the worst outcome available: every
        // endpoint test would then fail on a publish, thirty seconds later,
        // naming a message rather than a permission.
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not widen {user}'s broker permissions for the harness "
                + $"(exit {result.ExitCode}). stdout: {result.Stdout} stderr: {result.Stderr}");
        }

        // The mapped file rather than the container, because it is the same
        // text the broker imported and it can be read before anything starts.
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

    /// <summary>
    /// The connection each §7.1 identity would hold, pointed at Payments's own
    /// database rather than the container's <c>master</c>.
    /// </summary>
    public string ConnectionString { get; private set; } = null!;

    public PaymentsApiFactory Factory { get; private set; } = null!;

    /// <summary>
    /// The provider, loading the simulator's mappings (§14.1) so the host is
    /// answered by the stubs the local stack runs. Its log is what a test
    /// counts charges by.
    /// </summary>
    public WireMockServer Provider { get; private set; } = null!;

    /// <summary>The host's record of the order, observed (spec, section 6).</summary>
    public ObservedOrderStore Orders => Factory.Orders;

    /// <summary>
    /// Fails the host's next commit that has an outbox row staged, once.
    /// Disposing the returned fault disarms it.
    /// </summary>
    public CommitFault FailNextCommit() => Factory.CommitFaults.Arm();

    /// <summary>
    /// Messages a queue holds, read from the broker itself, or zero when it
    /// does not exist yet — MassTransit declares an <c>_error</c> queue on its
    /// first fault, and a fault's arrival there is an outcome no table shows.
    /// </summary>
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
        // The image §14.1 builds, from the same Dockerfile. A name of this
        // fixture's own, as Ordering's argues: Testcontainers writes the build
        // context to a file named after the image, and two suites building one
        // name at once race on that file. The layers are shared, so the second
        // build is a cache hit; WithCleanUp(false) keeps the plugin download to
        // once per machine.
        IFutureDockerImage broker = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(BrokerContextPath())
            .WithDockerfile("Dockerfile")
            .WithName("ashamray-test-broker-payments:4.1-delayed")
            .WithCleanUp(false)
            .Build();

        // The service's own account: the built image carries definitions.json,
        // so the container starts with exactly the grant §14.1 deploys. The
        // password is §14.1's local-development default.
        _rabbit = new RabbitMqBuilder()
            .WithImage(broker)
            .WithUsername("payments-svc")
            .WithPassword("local-dev-payments")
            .Build();

        await broker.CreateAsync(TestContext.Current.CancellationToken);

        Provider = WireMockServer.Start();
        Provider.ReadStaticMappings(SimulatorMappings.Directory());

        // Together, §12.4's printed shape — the broker's start hides inside
        // SQL Server's, which is the slower of the two by some margin.
        await Task.WhenAll(
            _sql.StartAsync(TestContext.Current.CancellationToken),
            _rabbit.StartAsync(TestContext.Current.CancellationToken));

        await WidenWriteForTheHarnessAsync();

        // The container hands out a connection to master; Payments owns a
        // database of its own (§7.1), and MigrateAsync is what creates it.
        // DbConnectionStringBuilder out of habit rather than necessity now:
        // this project does carry the provider package, for the open
        // SqlConnection Respawn inspects in ResetAsync.
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = "Payments";
        ConnectionString = connection.ConnectionString;

        FirstRunExitCode = await RunMigratorAsync(ConnectionString);

        Factory = new PaymentsApiFactory(ConnectionString, _rabbit.GetConnectionString(), Provider.Urls[0] + "/");

        // A table for the transaction tests, created here and not in a
        // migration. It is a fixture of the test rather than a table of the
        // service, and putting it in a migration to make a test easier would
        // ship it to production.
        await ExecuteAsync(
            """
            CREATE TABLE payments.TransactionProbe
            (
                Id   uniqueidentifier NOT NULL PRIMARY KEY,
                Note nvarchar(100)    NOT NULL
            );
            """);
    }

    /// <summary>
    /// §12.4's reset: truncation over the <c>payments</c> schema, far faster
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
                SchemasToInclude = ["payments"]
            });

        await _respawner.ResetAsync(connection);

        // The mappings as well as the log, and the mappings are the half that
        // bites. A test that stubs an answer — a 409 on the void key, say —
        // adds a mapping the log reset does not touch, so it keeps answering
        // for every test that runs after it in this collection. That is
        // invisible where it is caused and shows up as unrelated tests failing
        // in whatever order xUnit chose, which is the shape of a defect nobody
        // can reproduce in isolation. Re-reading the simulator's own mappings
        // (§14.1) is what makes the baseline the same for every test.
        Provider.ResetMappings();
        Provider.ReadStaticMappings(SimulatorMappings.Directory());
        Provider.ResetScenarios();
        Provider.ResetLogEntries();
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
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

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
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await db.Database
            .SqlQueryRaw<T>(sql, parameters)
            .SingleAsync(TestContext.Current.CancellationToken);
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
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

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

    /// <summary>
    /// Seeds a prior attempt count through the same column the dispatcher
    /// writes. Explicit rather than hidden in a builder, so no state carries
    /// between tests (§12.8).
    /// </summary>
    public Task SetOutboxAttemptsAsync(Guid messageId, int attempts) =>
        ExecuteAsync(
            "UPDATE payments.OutboxMessages SET Attempts = {0} WHERE MessageId = {1};",
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
            "UPDATE payments.OutboxMessages SET Lane = {0} WHERE MessageId = {1};",
            lane.ToString(),
            messageId);

    /// <summary>
    /// Clears retry backoff leases so the next pass is gated only by the
    /// attempt cap. Lets a test distinguish "backed off" from "abandoned"
    /// without sleeping.
    /// </summary>
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

    /// <summary>
    /// The inbox rows one message wrote, untracked (§9.5) — the read an
    /// assertion about the filter wants, which <see cref="InboxAsync()"/>
    /// cannot give. An unscoped read makes every assertion two claims at
    /// once: that the duplicate was suppressed, and that no other row
    /// exists anywhere in the schema — a property of test isolation, not of
    /// <c>InboxFilter&lt;T&gt;</c>, and the one that breaks when
    /// <c>IntegrationCollection</c> classes share this fixture in sequence.
    /// </summary>
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

    /// <summary>
    /// Writes inbox rows directly, for tests about the purge rather than the
    /// filter. The filter's own tests go through a consume pipeline, because
    /// what they are about is which of <c>MessageId</c> and <c>Endpoint</c> the
    /// row is keyed on and when it is committed.
    /// </summary>
    public async Task StageInboxAsync(params InboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

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
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        db.IdempotencyMarkers.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// §2's registered claim store — <c>NoClaimsIdempotencyStore</c>, which
    /// never holds a key, so <c>UnheldAsync</c> reports every marker unheld
    /// (ADR-039) and a claim attempt throws.
    /// </summary>
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
            "UPDATE payments.OutboxMessages SET ProcessedAt = {0} WHERE MessageId = {1};",
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
    /// in the marker's leg wide enough to reach the window the split opened
    /// or to stand a key still held (ADR-039) — the registered store never
    /// holds one. <c>UnheldAsync</c> is called between the <c>SELECT</c> and
    /// the <c>DELETE</c>, exactly where a replacement lands in production, so
    /// a decorator that mutates the table there puts a test on the far side
    /// of that window deterministically, without a fake clock or a second
    /// connection racing the first.
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
    /// One pass under a policy of the test's own and a registered clock
    /// moved forward by <paramref name="skew"/>, because nothing else in
    /// this suite can tell the marker's cutoff from the other two: the
    /// outbox's and inbox's are computed by the application against the
    /// registered <c>TimeProvider</c>, the marker's by the server
    /// (ADR-038). Moving only the registered clock and leaving the
    /// server's alone is what proves a pass reads each statement's own
    /// clock rather than assuming it.
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
    /// Deletes the marker under <paramref name="key"/> and writes a fresh
    /// one back under the same key and the same <c>CommittedAt</c> — the ABA
    /// a purge pass can meet between its <c>SELECT</c> and its
    /// <c>DELETE</c>, staged at its worst. Preserving the timestamp
    /// reproduces ADR-041's coincidence — a clock set to the exact tick of a
    /// row past its window — without touching the container's clock. The
    /// <c>rowversion</c> is not carried across: SQL Server generates it, and
    /// a replacement getting a new one is the property being tested.
    /// </summary>
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

    /// <summary>Markers §8.5 holds for one key — nought or one, and which is the point.</summary>
    public Task<int> IdempotencyMarkerCountAsync(string key) =>
        ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM payments.IdempotencyMarkers WHERE [Key] = {0}",
            key);

    /// <summary>Rows the transaction probe holds for one id.</summary>
    public Task<int> ProbeRowCountAsync(Guid id) =>
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.TransactionProbe WHERE Id = {0}", id);

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
                    Provider?.Stop();
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
