using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using WireMock.Server;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>The poll against a down or slow carrier, on a host of its own, so its breaker is fresh.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class TrackingFaultTests : IAsyncLifetime
{
    private readonly ServiceFixture _fixture;
    private readonly WireMockServer _carrier = ServiceFixture.StartCarrier();
    private readonly ShippingWorkerFactory _host;

    public TrackingFaultTests(ServiceFixture fixture)
    {
        _fixture = fixture;
        _host = fixture.NewWorkerHost(_carrier.Urls[0] + "/");
    }

    public ValueTask InitializeAsync() => new(_fixture.ResetAsync());

    public ValueTask DisposeAsync()
    {
        try
        {
            _host.Dispose();
        }
        finally
        {
            _carrier.Stop();
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_failing_carrier_backs_the_row_off_and_leaves_it()
    {
        // Repointed at a reference this host answers 503 on, which is what a carrier that is down looks like to a poll.
        Shipment shipment = await _fixture.BookedAsync("SIM-TRANSIT");
        await _fixture.SetCarrierReferenceAsync(shipment.Id, "crr_down");
        using IDisposable down = ServiceFixture.CarrierAnswers(
            _carrier, "/v1/shipments/crr_down/events", 503);

        // From the engine's clock and before the pass, since FailSql stamps NextPollAt from SYSDATETIMEOFFSET().
        DateTimeOffset before = await _fixture.DatabaseNowAsync();

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        (await _fixture.StatusAsync(shipment.Id)).ShouldBe("Booked", "an outage is never an answer");
        (await _fixture.PollAttemptsAsync(shipment.Id)).ShouldBe(1);
        (await _fixture.AttemptsAsync(shipment.Id)).ShouldBe(0, "the fulfilment pass's ladder is its own (ADR-054)");
        (await _fixture.LockedUntilAsync(shipment.Id)).ShouldBeNull("a backed-off row is released, not held");
        (await _fixture.NextPollAtAsync(shipment.Id)).ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(
            before + CarrierHop.TrackingPollInterval,
            "the ladder's first step is five seconds, and a failed poll is never due sooner than a healthy one");
    }

    [Fact]
    public async Task A_slow_carrier_still_has_every_claimed_row_polled_in_one_pass()
    {
        // Four seconds over a batch of five: polled one after another, reserving a hop's total per row, the pass would
        // start two calls; polled together, every call starts at the claim.
        const int Batch = 5;
        _carrier.AddGlobalProcessingDelay(TimeSpan.FromSeconds(4));

        for (int i = 0; i < Batch; i++)
            await _fixture.BookedAsync("SIM-TRANSIT");

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(Batch);

        (await _fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE Status = 'Dispatched' AND LockedUntil IS NULL"))
            .ShouldBe(Batch, "each row's page was applied and its claim released by the commit that used it");
    }

    private TrackingWorker Worker() => _host.Services.GetRequiredService<TrackingWorker>();
}
