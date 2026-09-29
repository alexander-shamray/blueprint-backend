using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using WireMock.Server;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// The poll's transient row, over a host of its own because the breaker it
/// fills is sized to open (<c>CarrierHop</c>). The database, the broker and
/// the Ordering stub stay the collection's.
/// </summary>
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
        // Booked through the collection's own host and then repointed at a
        // reference this host answers 503 on, which is what the carrier being
        // down looks like to a poll: nothing about the row is wrong. The
        // mapping is registered here and not in the simulator's directory —
        // that directory is the postal-code script a person at the keyboard
        // drives (spec, section 9), and a dead events feed is no script.
        Shipment shipment = await _fixture.BookedAsync("SIM-TRANSIT");
        await _fixture.SetCarrierReferenceAsync(shipment.Id, "crr_down");
        using IDisposable down = ServiceFixture.CarrierAnswers(
            _carrier, "/v1/shipments/crr_down/events", 503);

        // Captured before the pass, and from the engine's clock, because
        // FailSql stamps NextPollAt from SYSDATETIMEOFFSET() at the moment of
        // the update: an instant read after the pass would make the delay read
        // short, and one read from the host would carry its skew.
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

    private TrackingWorker Worker() => _host.Services.GetRequiredService<TrackingWorker>();
}
