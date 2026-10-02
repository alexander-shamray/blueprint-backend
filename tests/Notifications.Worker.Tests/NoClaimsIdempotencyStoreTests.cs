using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>Notifications' own store never claims or holds, so ADR-039's purge resolves one without Redis.</summary>
public sealed class NoClaimsIdempotencyStoreTests
{
    private static NotificationsWorkerFactory Factory() =>
        new(
            "Server=sql.invalid;Database=Notifications;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://notifications-svc:x@rabbit.invalid:5672");

    [Fact]
    public async Task UnheldAsync_reports_every_key_it_is_given()
    {
        using NotificationsWorkerFactory factory = Factory();
        IIdempotencyStore store = factory.Services.GetRequiredService<IIdempotencyStore>();

        string[] keys = ["a", "b"];

        IReadOnlyCollection<string> unheld = await store.UnheldAsync(keys, TestContext.Current.CancellationToken);

        unheld.ShouldBe(keys, ignoreOrder: true);
    }

    [Fact]
    public async Task A_claim_attempt_throws()
    {
        using NotificationsWorkerFactory factory = Factory();
        IIdempotencyStore store = factory.Services.GetRequiredService<IIdempotencyStore>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.TryClaimAsync("k", TimeSpan.FromHours(1), TestContext.Current.CancellationToken));
    }
}
