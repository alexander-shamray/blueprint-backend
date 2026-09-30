using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>Shipping's own store never claims or holds, so ADR-039's purge resolves one without Redis.</summary>
public sealed class NoClaimsIdempotencyStoreTests
{
    private static ShippingWorkerFactory Factory() =>
        new(
            "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://shipping-svc:x@rabbit.invalid:5672");

    [Fact]
    public async Task UnheldAsync_reports_every_key_it_is_given()
    {
        using ShippingWorkerFactory factory = Factory();
        IIdempotencyStore store = factory.Services.GetRequiredService<IIdempotencyStore>();

        string[] keys = ["a", "b"];

        IReadOnlyCollection<string> unheld = await store.UnheldAsync(keys, TestContext.Current.CancellationToken);

        unheld.ShouldBe(keys, ignoreOrder: true);
    }

    [Fact]
    public async Task A_claim_attempt_throws()
    {
        using ShippingWorkerFactory factory = Factory();
        IIdempotencyStore store = factory.Services.GetRequiredService<IIdempotencyStore>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.TryClaimAsync("k", TimeSpan.FromHours(1), TestContext.Current.CancellationToken));
    }
}
