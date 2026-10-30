using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>A configured pace reaches the claim's statement, not only the options it was bound to.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SendPacingClaimTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_send_claim_takes_the_configured_batch_under_the_configured_lease()
    {
        // Three notices due, and a batch of two under a lease no default comes near.
        for (int i = 0; i < 3; i++)
            await fixture.PendingAsync(TemplateKeys.OrderConfirmed, Guid.CreateVersion7());

        SendPacingOptions pace = new() { SendClaimBatchSize = 2, SendLeaseSeconds = 600 };
        SendClaims claims = new(
            fixture.Factory.Services.GetRequiredService<IDbConnectionFactory>(),
            Options.Create(pace));

        DateTimeOffset before = await fixture.DatabaseNowAsync();
        IReadOnlyList<SendWork> claimed = await claims.ClaimAsync(false, TestContext.Current.CancellationToken);

        claimed.Count.ShouldBe(2);
        claimed.ShouldAllBe(work => work.LockedUntil >= before.AddSeconds(pace.SendLeaseSeconds));
    }

    [Fact]
    public async Task The_send_claim_takes_the_longest_due_first_whatever_order_the_index_keeps()
    {
        for (int i = 0; i < 3; i++)
            await fixture.PendingAsync(TemplateKeys.OrderConfirmed, Guid.CreateVersion7());

        // Due times opposite to the claim index's key order, so a claim reading it unsorted takes the wrong row.
        await fixture.ExecuteAsync(
            """
            UPDATE n
            SET NextAttemptAt = DATEADD(minute, -k.Position, SYSDATETIMEOFFSET())
            FROM notifications.NotificationLog n
            INNER JOIN (
                SELECT NotificationId, Position = ROW_NUMBER() OVER (ORDER BY NotificationId)
                FROM notifications.NotificationLog) k
                ON k.NotificationId = n.NotificationId;
            """);
        Guid longestDue = await fixture.ScalarAsync<Guid>(
            "SELECT TOP (1) Value = NotificationId FROM notifications.NotificationLog ORDER BY NotificationId DESC");

        SendClaims claims = new(
            fixture.Factory.Services.GetRequiredService<IDbConnectionFactory>(),
            Options.Create(new SendPacingOptions { SendClaimBatchSize = 1 }));
        IReadOnlyList<SendWork> claimed = await claims.ClaimAsync(false, TestContext.Current.CancellationToken);

        claimed.ShouldHaveSingleItem().NotificationId.ShouldBe(longestDue);
    }
}
