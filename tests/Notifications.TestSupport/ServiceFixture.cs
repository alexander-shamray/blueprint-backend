using Notifications.Infrastructure.Persistence;
using Notifications.Migrator;
using Common.Contracts;
using Common.TestSupport;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Records;
using Xunit;
using MessagingRegistration = Notifications.Infrastructure.Messaging.DependencyInjection;

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

    /// <summary>The messages waiting in one queue, or zero when the broker has never declared it.</summary>
    public async Task<int> QueueDepthAsync(string queue)
    {
        string[]? row = (await BrokerRowsAsync(["list_queues", "name", "messages"]))
            .SingleOrDefault(columns => columns.Length == 2 && columns[0] == queue);

        return row is null ? 0 : int.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture);
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

    protected override NotificationsWorkerFactory CreateFactory() => new(ConnectionString, BrokerConnectionString);
}
