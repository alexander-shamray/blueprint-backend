using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>A configured pace reaches the claim's statement, not only the options it was bound to.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class CarrierPacingClaimTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_tracking_claim_takes_the_configured_batch_under_the_configured_lease()
    {
        // Three rows due, and a batch of two under a lease no default comes near.
        for (int i = 0; i < 3; i++)
            await fixture.BookedAsync("SIM-TRANSIT");

        CarrierPacingOptions pace = new() { TrackingClaimBatchSize = 2, TrackingLeaseSeconds = 600 };
        TrackingClaims claims = new(
            fixture.Factory.Services.GetRequiredService<IDbConnectionFactory>(),
            Options.Create(pace));

        DateTimeOffset before = await fixture.DatabaseNowAsync();
        IReadOnlyList<TrackingWork> claimed = await claims.ClaimAsync(TestContext.Current.CancellationToken);

        claimed.Count.ShouldBe(2);
        claimed.ShouldAllBe(work => work.LockedUntil >= before.AddSeconds(pace.TrackingLeaseSeconds));
    }
}
