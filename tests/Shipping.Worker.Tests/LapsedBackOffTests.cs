using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>A pass whose lease lapsed backs off nobody: the reclaimer's lease and count are its own.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class LapsedBackOffTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_lapsed_tracking_pass_leaves_the_reclaimers_lease_and_count_alone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        TrackingClaims claims = scope.ServiceProvider.GetRequiredService<TrackingClaims>();

        TrackingWork lapsed = (await claims.ClaimAsync(ct)).Single(w => w.Id == shipment.Id.Value);
        await fixture.ExpireLeasesAsync();
        await PastTheClaimAsync(lapsed.LockedUntil, TrackingWorker.LeaseSeconds);
        TrackingWork holder = (await claims.ClaimAsync(ct)).Single(w => w.Id == shipment.Id.Value);
        holder.LockedUntil.ShouldNotBe(lapsed.LockedUntil, "two passes' leases, or the test proves nothing");

        await claims.FailAsync(lapsed.Id, lapsed.LockedUntil, ct);

        (await fixture.LockedUntilAsync(shipment.Id)).ShouldBe(holder.LockedUntil, "the reclaimer still holds it");
        (await fixture.PollAttemptsAsync(shipment.Id)).ShouldBe(0, "a lapsed pass adds nothing to another's count");

        await claims.FailAsync(holder.Id, holder.LockedUntil, ct);

        (await fixture.PollAttemptsAsync(shipment.Id)).ShouldBe(1, "the holder's own backoff still lands");
        (await fixture.LockedUntilAsync(shipment.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_lapsed_fulfilment_pass_leaves_the_reclaimers_lease_and_count_alone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.RequestCancellationAsync(shipment.Id);
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        FulfilmentClaims claims = scope.ServiceProvider.GetRequiredService<FulfilmentClaims>();

        FulfilmentWork lapsed = (await claims.ClaimAsync(ct)).Single(w => w.Id == shipment.Id.Value);
        await fixture.ExpireLeasesAsync();
        await PastTheClaimAsync(lapsed.LockedUntil, FulfilmentWorker.LeaseSeconds);
        FulfilmentWork holder = (await claims.ClaimAsync(ct)).Single(w => w.Id == shipment.Id.Value);
        holder.LockedUntil.ShouldNotBe(lapsed.LockedUntil, "two passes' leases, or the test proves nothing");

        await claims.FailAsync(lapsed.Id, lapsed.LockedUntil, ct);

        (await fixture.LockedUntilAsync(shipment.Id)).ShouldBe(holder.LockedUntil, "the reclaimer still holds it");
        (await fixture.AttemptsAsync(shipment.Id)).ShouldBe(0, "a lapsed pass adds nothing to another's count");

        await claims.FailAsync(holder.Id, holder.LockedUntil, ct);

        (await fixture.AttemptsAsync(shipment.Id)).ShouldBe(1, "the holder's own backoff still lands");
        (await fixture.LockedUntilAsync(shipment.Id)).ShouldBeNull();
    }

    // A real reclaim follows a lapse of the whole lease, so its stamp is always later. This test lapses the lease by
    // hand within milliseconds, and the engine's clock can read one tick for both claims, so it waits that tick out.
    private Task PastTheClaimAsync(DateTimeOffset firstLease, int leaseSeconds) =>
        ServiceFixture.WaitUntilAsync(
            async () => await fixture.DatabaseNowAsync() > firstLease.AddSeconds(-leaseSeconds));
}
