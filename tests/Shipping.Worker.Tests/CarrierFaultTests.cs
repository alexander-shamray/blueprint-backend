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

/// <summary>
/// Section 9's transient rows, each over a host of its own because the
/// breaker they fill is sized to open (<c>CarrierHop</c>), and because a
/// stalled answer outlives the test that asked for it.
/// </summary>
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
        // Stubbed rather than scripted: section 9's table names all three and
        // the simulator scripts one, so without this a branch that dropped
        // either of the others would leave the suite green.
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
        counted.Value.ShouldBe(CarrierHop.MaxRetryAttempts + 1,
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
        // CarrierAnswerBuffer reads the body inside the attempt, so a body over
        // the bound fails the attempt rather than the call: the pipeline
        // retries it and the breaker remembers it, which is why this row is
        // here and not on the shared host.
        Server.Given(Request.Create().WithPath("/v1/shipments/crr_x/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("{\"events\":[" + new string('x', CarrierHop.MaxAnswerBytes + 1) + "]}"));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().GetEventsAsync("crr_x", TestContext.Current.CancellationToken));

        Calls("/v1/shipments/crr_x/events").ShouldBe(CarrierHop.MaxRetryAttempts + 1,
            "an oversize body is an attempt the pipeline retries, not an answer the adapter refuses");
    }

    [Fact]
    public async Task An_open_circuit_makes_no_call_at_all()
    {
        // The breaker sits inside the retry, so one call is
        // MaxRetryAttempts + 1 attempts against the minimum throughput, and a
        // fresh host is what makes that arithmetic this test's alone.
        CancellationToken ct = TestContext.Current.CancellationToken;
        while (Calls("/v1/shipments") < CarrierHop.CircuitBreakerMinimumThroughput)
        {
            await Should.ThrowAsync<CarrierUnavailableException>(() => Carrier().BookAsync(Booking("SIM-DOWN"), ct));
        }

        int before = Calls("/v1/shipments");

        await Should.ThrowAsync<CarrierUnavailableException>(() => Carrier().BookAsync(Booking("SIM-DOWN"), ct));

        // The half that makes it a breaker rather than a slow failure: once
        // open it refuses without a request leaving this process, which is
        // what stops a worker hammering a carrier that is already down.
        Calls("/v1/shipments").ShouldBe(before);
    }
}
