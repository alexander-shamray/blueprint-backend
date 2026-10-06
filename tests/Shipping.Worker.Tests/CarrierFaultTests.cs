using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.TestSupport;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>The carrier's transient answers, each on its own host, since the breaker they fill opens.</summary>
public sealed class CarrierFaultTests : IDisposable
{
    private readonly HttpCarrierGatewayTests.CarrierHost _host = new();

    public CarrierFaultTests() =>
        _host.Server.ReadStaticMappings(SimulatorMappings.Directory());

    public void Dispose() => _host.Dispose();

    private WireMockServer Server => _host.Server;

    private ICarrierGateway Carrier() =>
        _host.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<ICarrierGateway>();

    private int Calls(string path) =>
        Server.LogEntries.Count(e => e.RequestMessage!.Path == path);

    private static BookingRequest Booking(string postalCode) =>
        new(ShipmentId.New(), new DeliveryAddress("1 Abay Avenue", null, "Almaty", postalCode, "KZ"));

    [Fact]
    public async Task A_503_is_retried_in_the_client_then_thrown_as_unavailable_and_counted_per_attempt()
    {
        using OutboundCount counted = OutboundCounter.Unavailable(_host.Factory.Services);

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("SIM-DOWN"), TestContext.Current.CancellationToken));

        Calls("/v1/shipments").ShouldBe(CarrierHop.MaxRetryAttempts + 1);
        counted.Value.ShouldBe(CarrierHop.MaxRetryAttempts + 1, "one per failing attempt, not one per call");
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task A_timeout_a_throttle_or_a_server_fault_is_retried_then_thrown_as_unavailable(int status)
    {
        // Stubbed, since the simulator scripts one of the three and a branch dropping another would stay green.
        Server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("050000"), TestContext.Current.CancellationToken));

        Calls("/v1/shipments").ShouldBe(CarrierHop.MaxRetryAttempts + 1);
    }

    [Fact]
    public async Task A_stalled_carrier_is_unavailable_within_the_total_budget_and_its_timeouts_count()
    {
        using OutboundCount counted = OutboundCounter.Unavailable(_host.Factory.Services);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("SIM-SLOW"), TestContext.Current.CancellationToken));

        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(CarrierHop.TotalRequestTimeout + TimeSpan.FromSeconds(2));
        counted.Value.ShouldBe(
            CarrierHop.MaxRetryAttempts + 1,
            "each attempt timeout is the carrier's, counted once, by OnTimeout");
    }

    [Fact]
    public async Task A_cancellation_during_an_attempt_is_the_callers_and_is_not_counted()
    {
        using OutboundCount counted = OutboundCounter.Unavailable(_host.Factory.Services);
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancelled.CancelAfter(TimeSpan.FromSeconds(1));

        // The stalled script, so the cancellation lands inside an attempt,
        // where it and an attempt timeout arrive as the same exception.
        await Should.ThrowAsync<OperationCanceledException>(() =>
            Carrier().BookAsync(Booking("SIM-SLOW"), cancelled.Token));

        counted.Value.ShouldBe(0, "the caller cancelling mid-attempt is not a carrier incident");
    }

    [Fact]
    public async Task An_answer_larger_than_the_bound_is_refused_before_it_is_read_and_the_attempt_is_retried()
    {
        // CarrierAnswerBuffer reads the body inside the attempt, so an oversize body fails the attempt and is retried.
        Server.Given(Request.Create().WithPath("/v1/shipments/crr_x/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("{\"events\":[" + new string('x', CarrierHop.MaxAnswerBytes + 1) + "]}"));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().GetEventsAsync("crr_x", TestContext.Current.CancellationToken));

        Calls("/v1/shipments/crr_x/events").ShouldBe(
            CarrierHop.MaxRetryAttempts + 1,
            "an oversize body is an attempt the pipeline retries, not an answer the adapter refuses");
    }

    [Fact]
    public async Task An_open_circuit_makes_no_call_at_all()
    {
        // The breaker sits inside the retry, so one call is MaxRetryAttempts + 1 attempts toward the throughput.
        CancellationToken ct = TestContext.Current.CancellationToken;
        while (Calls("/v1/shipments") < CarrierHop.CircuitBreakerMinimumThroughput)
        {
            await Should.ThrowAsync<CarrierUnavailableException>(() => Carrier().BookAsync(Booking("SIM-DOWN"), ct));
        }

        int before = Calls("/v1/shipments");

        await Should.ThrowAsync<CarrierUnavailableException>(() => Carrier().BookAsync(Booking("SIM-DOWN"), ct));

        // Once open, it refuses without a request leaving this process.
        Calls("/v1/shipments").ShouldBe(before);
    }
}
