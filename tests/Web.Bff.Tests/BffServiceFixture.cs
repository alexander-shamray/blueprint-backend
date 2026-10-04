using Common.Contracts;
using Common.TestSupport;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Web.Bff.Migrator;
using Web.Bff.Persistence;
using Xunit;
using MessagingRegistration = Web.Bff.Messaging.DependencyInjection;

namespace Web.Bff.Tests;

/// <summary>The BFF's names, migrator and factory over the shared body (ADR-056).</summary>
public sealed class BffServiceFixture()
    : ServiceFixture<BffFactory, Program, BffDbContext>("Bff")
{
    /// <summary>How long a staged step may take; a deadline, not a sleep, so a prompt step costs nothing.</summary>
    public static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Bff",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    /// <summary>Sends an event to <c>bff-order-events</c> as the account may, then awaits its inbox row.</summary>
    /// <remarks>
    /// To the queue, never a contract's exchange: <c>bff-svc</c> writes none (ADR-036), and nothing here widens it,
    /// so what the endpoint binds under this account is what the shipped grant allows.
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

        // The inbox row is written after the handler's statement commits (§9.5), so its rows are there to read.
        await WaitUntilAsync(async () => (await InboxAsync(message.MessageId)).Count == 1);
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

    /// <summary>One order's row, untracked, or null.</summary>
    public async Task<ProjectedOrder?> OrderAsync(Guid orderId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        BffDbContext db = scope.ServiceProvider.GetRequiredService<BffDbContext>();

        return await db.Database
            .SqlQuery<ProjectedOrder>(
                $"""
                SELECT OrderId, CustomerId, Currency, TotalAmount, PlacedAt, ConfirmedAt, DispatchedAt,
                    DeliveredAt, CancelledAt, CancelOutcome, AuthorisedAt, AuthorisedAmount, RefundedAt,
                    RefundedAmount, PaymentCurrency, TrackingNumber, FirstSeenAt, AsOf
                FROM bff.Orders
                WHERE OrderId = {orderId}
                """)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One order's lines, in their numbered order.</summary>
    public async Task<IReadOnlyList<ProjectedLine>> LinesAsync(Guid orderId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        BffDbContext db = scope.ServiceProvider.GetRequiredService<BffDbContext>();

        return await db.Database
            .SqlQuery<ProjectedLine>(
                $"""
                SELECT LineNumber, ProductId, Quantity, UnitPrice
                FROM bff.OrderLines
                WHERE OrderId = {orderId}
                """)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    protected override BffFactory CreateFactory() =>
        new() { DatabaseConnectionString = ConnectionString, BrokerConnectionString = BrokerConnectionString };
}
