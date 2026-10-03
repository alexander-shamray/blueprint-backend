using System.Text.Json.Nodes;
using Notifications.Infrastructure.Persistence;
using Notifications.Migrator;
using Common.Contracts;
using Common.TestSupport;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Retention;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using Xunit;
using MessagingRegistration = Notifications.Infrastructure.Messaging.DependencyInjection;
using Response = WireMock.ResponseBuilders.Response;

namespace Notifications.TestSupport;

/// <summary>Notifications's names, migrator and factory over the shared body (ADR-056).</summary>
public sealed class ServiceFixture()
    : ServiceFixture<NotificationsWorkerFactory, Program, NotificationsDbContext>("Notifications")
{
    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Notifications",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    /// <summary>The column names of one table, from the engine rather than from the model.</summary>
    public async Task<string[]> ColumnsAsync(string schema, string table)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        return await db.Database
            .SqlQuery<string>(
                $"""
                SELECT COLUMN_NAME AS Value
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = {schema} AND TABLE_NAME = {table}
                """)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>How long a staged step may take; a deadline, not a sleep, so a prompt step costs nothing.</summary>
    public static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Sends an event to <c>notifications-events</c> as the account may, then awaits its inbox row.</summary>
    /// <remarks>
    /// To the queue, never a contract's exchange: <c>notifications-svc</c> writes none (ADR-036), and nothing here
    /// widens it, so what the endpoint binds under this account is what the shipped grant allows.
    /// </remarks>
    public async Task DeliverAsync<T>(T message)
        where T : class, IIntegrationEvent
    {
        // Bounded, because a send the broker refuses is retried rather than failed.
        using CancellationTokenSource bounded =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(StepDeadline);

        ISendEndpoint endpoint = await Factory.Services.GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri($"queue:{MessagingRegistration.EventsQueue}"));

        await endpoint.Send(
            message,
            c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            },
            bounded.Token);

        // The inbox row is written after the consumer's command commits (§9.5), so its rows are there to read.
        await WaitUntilAsync(async () => (await InboxAsync(message.MessageId)).Count == 1);
    }

    /// <summary>Every notice owed for one order, untracked.</summary>
    public async Task<IReadOnlyList<Notification>> NotificationsAsync(Guid orderId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        return await db.NotificationLog
            .AsNoTracking()
            .Where(n => n.OrderId == orderId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The order record for one order, untracked, or null.</summary>
    public async Task<OrderRecord?> OrderRecordAsync(Guid orderId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        return await db.OrderRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.OrderId == orderId, TestContext.Current.CancellationToken);
    }

    /// <summary>The exchanges bound to one queue, read from the broker itself.</summary>
    public async Task<string[]> BindingsAsync(string queue) =>
    [
        .. (await BrokerRowsAsync(["list_bindings", "source_name", "destination_name"]))
            .Where(columns => columns.Length == 2 && columns[1] == queue)
            .Select(columns => columns[0])
    ];

    /// <summary>One account's grant on the default vhost, as the broker holds it rather than as a file says.</summary>
    public async Task<(string Configure, string Write, string Read)> BrokerPermissionsAsync(string user)
    {
        string[] row = (await BrokerRowsAsync(["list_permissions"]))
            .Single(columns => columns.Length == 4 && columns[0] == user);

        return (row[1], row[2], row[3]);
    }

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

    /// <summary>The relay, Compose's image, read back through its API (§14.1).</summary>
    public Mailpit Relay { get; } = Mailpit.Plain();

    /// <summary>Keycloak's admin API on loopback, the owner ADR-052's contact read asks.</summary>
    public WireMockServer Keycloak { get; private set; } = null!;

    /// <summary>Every line the host has logged since the last reset.</summary>
    public CapturedLogs CapturedLogs => Factory.CapturedLogs;

    /// <summary>Fails the host's next commit that marks a notice sent, once; disposing the fault disarms it.</summary>
    public CommitFault FailNextSentCommit() => Factory.CommitFaults.Arm();

    /// <summary>A second host over the same database, broker and stubs, with a breaker of its own.</summary>
    public NotificationsWorkerFactory NewWorkerHost(string? relayHost = null, int? relayPort = null) =>
        new(
            ConnectionString,
            BrokerConnectionString,
            mailHost: relayHost ?? Relay.Host,
            mailPort: relayPort ?? Relay.Port,
            contactSourceBaseUrl: Keycloak.Urls[0] + "/");

    protected override NotificationsWorkerFactory CreateFactory() => NewWorkerHost();

    protected override async Task StartStubsAsync()
    {
        Keycloak = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });

        // Asked once first, since a cold stub's first answer can outlast ContactHop's total.
        using HttpClient warm = new();
        using HttpResponseMessage answered =
            await warm.GetAsync(Keycloak.Urls[0] + "/", TestContext.Current.CancellationToken);
        Keycloak.ResetLogEntries();

        await Relay.StartAsync(TestContext.Current.CancellationToken);
    }

    // A mapping a test adds outlives a log reset, and a logged line would reach the next test.
    protected override void ResetStubs()
    {
        Keycloak.Reset();
        CapturedLogs.Clear();
    }

    protected override async ValueTask DisposeStubsAsync()
    {
        try
        {
            Keycloak?.Stop();
        }
        finally
        {
            await Relay.DisposeAsync();
        }
    }

    /// <summary>The schema, the stubs and the relay's messages and Chaos triggers, for a suite that sends.</summary>
    public async Task ResetWithRelayAsync()
    {
        await ResetAsync();
        await Relay.ResetAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Messages a queue holds, read from the broker, or zero when the queue does not exist yet.</summary>
    public async Task<int> QueueDepthAsync(string queue)
    {
        foreach (string[] columns in await BrokerRowsAsync(["list_queues", "name", "messages"]))
        {
            if (columns.Length == 2 && columns[0] == queue)
                return int.Parse(columns[1], System.Globalization.CultureInfo.InvariantCulture);
        }

        return 0;
    }

    /// <summary>The admin API's path for one user of the realm the host reads.</summary>
    public static string UserPath(Guid customer) =>
        $"/admin/realms/{NotificationsWorkerFactory.LocalRealm}/users/{customer:D}";

    /// <summary>Keycloak answering one user, with a name the adapter must never bind.</summary>
    public void ContactAnswers(Guid customer, string email, string? locale = null, TimeSpan? delay = null)
    {
        JsonObject user = new()
        {
            ["id"] = customer.ToString("D"),
            ["username"] = $"customer-{customer:N}",
            ["firstName"] = "Айгерім",
            ["enabled"] = true,
            ["email"] = email
        };

        if (locale is not null)
            user["attributes"] = new JsonObject { ["locale"] = new JsonArray(locale) };

        IResponseBuilder response = Response.Create()
            .WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithBody(user.ToJsonString());

        if (delay is { } stall)
            response = response.WithDelay(stall);

        Keycloak.Given(Request.Create().WithPath(UserPath(customer)).UsingGet()).RespondWith(response);
    }

    /// <summary>Keycloak answering one user with a status: a refusal, an outage, or, at 404, no such user.</summary>
    /// <remarks>A 404 carries the admin API's own error body, which ADR-052 reads as no such customer.</remarks>
    public void ContactAnswers(Guid customer, int status)
    {
        IResponseBuilder response = Response.Create().WithStatusCode(status);

        if (status == 404)
        {
            response = response
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"User not found"}""");
        }

        Keycloak.Given(Request.Create().WithPath(UserPath(customer)).UsingGet()).RespondWith(response);
    }

    /// <summary>How many times the host asked for one user.</summary>
    public int ContactCalls(Guid customer) =>
        Keycloak.LogEntries.Count(e => e.RequestMessage!.Path == UserPath(customer));

    /// <summary>A notice owed and due, as a consumer writes one, half a minute old on the host's clock.</summary>
    public async Task<Notification> PendingAsync(string templateKey, Guid order, string? storedParameters = null)
    {
        DateTimeOffset created = DateTimeOffset.UtcNow.AddSeconds(-30);
        string parameters = storedParameters ?? ParametersFormat.Write(
            new NotificationParameters { OrderId = order, OccurredAt = created });

        Notification notification =
            Notification.Pending(Guid.CreateVersion7(), templateKey, order, parameters, created);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        db.NotificationLog.Add(notification);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return notification;
    }

    /// <summary>Ordering's record of an order, as its first event writes it.</summary>
    public async Task OrderAsync(Guid order, Guid customer)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        db.OrderRecords.Add(OrderRecord.For(order, customer, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The record's cancellation, as <c>OrderCancelled</c>'s consumer sets it.</summary>
    public async Task CancelOrderAsync(Guid order, string? reason, string? origin)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        OrderRecord record = await db.OrderRecords
            .SingleAsync(r => r.OrderId == order, TestContext.Current.CancellationToken);

        if (!record.Cancel(reason, origin, DateTimeOffset.UtcNow))
            throw new InvalidOperationException($"Order {order} was already cancelled.");

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A stored contact fetched <paramref name="age"/> ago, through the store the worker reads.</summary>
    public async Task StageContactAsync(Guid customer, string email, string? locale, TimeSpan age)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IContactStore>().SaveAsync(
            customer,
            new ContactLookup.Found(email, locale),
            DateTimeOffset.UtcNow - age,
            TestContext.Current.CancellationToken);
    }

    /// <summary>The stored contact for one customer, or null.</summary>
    public async Task<ContactRecord?> ContactAsync(Guid customer)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactStore>()
            .GetAsync(customer, TestContext.Current.CancellationToken);
    }

    /// <summary>One notice as the table holds it now, untracked.</summary>
    public async Task<Notification> NotificationAsync(Guid notificationId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        return await db.NotificationLog
            .AsNoTracking()
            .SingleAsync(n => n.NotificationId == notificationId, TestContext.Current.CancellationToken);
    }

    /// <summary>Makes a backed-off notice due now on the engine's clock, which the claim compares against.</summary>
    public Task ClearBackoffAsync(Guid notificationId) =>
        ExecuteAsync(
            "UPDATE notifications.NotificationLog SET NextAttemptAt = DATEADD(second, -1, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            notificationId);

    /// <summary>Moves a notice's creation back by <paramref name="age"/>, which its give-up age is read from.</summary>
    public Task AgeAsync(Guid notificationId, TimeSpan age) =>
        ExecuteAsync(
            "UPDATE notifications.NotificationLog SET CreatedAt = DATEADD(second, -{1}, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            notificationId,
            (int)age.TotalSeconds);

    /// <summary>Runs exactly one pass of ADR-053's three windows, with no timer.</summary>
    public Task<(int Notifications, int Contacts, int Orders)> PurgeNotificationsRetentionAsync() =>
        Factory.Services
            .GetRequiredService<NotificationsRetentionService>()
            .PurgeAsync(TestContext.Current.CancellationToken);

    /// <summary>Runs exactly one send pass on the fixture's host, with no timers and no waiting.</summary>
    public Task<SendPass> RunSendPassAsync() =>
        Factory.Services.GetRequiredService<SendWorker>().RunOnceAsync(TestContext.Current.CancellationToken);

    /// <summary>Waits for the engine's clock to reach the order's pending rows, stamped by the host's.</summary>
    public Task WaitUntilDueAsync(Guid order) =>
        WaitUntilAsync(async () => await ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE OrderId = {0} " +
            "AND Status = 'Pending' AND NextAttemptAt > SYSDATETIMEOFFSET()",
            order) == 0);

    /// <summary>Runs passes, each pending row made due first, until none is pending or a bound is hit.</summary>
    public async Task SendUntilSettledAsync(int maxPasses = 10)
    {
        const string Pending = "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE Status = 'Pending'";

        for (int pass = 0; pass < maxPasses; pass++)
        {
            if (await ScalarAsync<int>(Pending) == 0)
                return;

            await ExecuteAsync(
                "UPDATE notifications.NotificationLog SET NextAttemptAt = DATEADD(second, -1, SYSDATETIMEOFFSET()) " +
                "WHERE Status = 'Pending';");
            await RunSendPassAsync();
        }

        if (await ScalarAsync<int>(Pending) == 0)
            return;

        throw new TimeoutException($"A notice was still pending after {maxPasses} passes.");
    }

    /// <summary>Seeds the failed passes a notice has had, through the column the backoff writes.</summary>
    public Task SetAttemptsAsync(Guid notificationId, int attempts) =>
        ExecuteAsync(
            "UPDATE notifications.NotificationLog SET Attempts = {1} WHERE NotificationId = {0};",
            notificationId,
            attempts);
}
