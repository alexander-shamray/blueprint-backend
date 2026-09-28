using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.TestSupport;
using Shouldly;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using Xunit;
using CarrierRegistration = Shipping.Infrastructure.Carrier.DependencyInjection;

namespace Shipping.Worker.Tests;

/// <summary>
/// The adapter over a real HTTP server loading the simulator's own mappings,
/// so the file Compose runs is the file these assert (§12: WireMock.Net for a
/// third-party API).
/// </summary>
public sealed class HttpCarrierGatewayTests : IClassFixture<HttpCarrierGatewayTests.CarrierHost>
{
    private const string UnreachableSql =
        "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true";

    private const string UnreachableRabbit = "amqp://shipping-svc:x@rabbit.invalid:5672";

    /// <summary>
    /// One server and one host for the class: a host over an unreachable
    /// broker can take seconds to stop, so only a test that needs a pipeline
    /// of its own builds one. Every case that leaves the breaker with a
    /// failure to remember is in <c>CarrierFaultTests</c> instead.
    /// </summary>
    public sealed class CarrierHost : IDisposable
    {
        public CarrierHost()
        {
            // Loopback, not WireMock's default of every interface: a socket on
            // 0.0.0.0 is what a workstation firewall stops to ask about, and the
            // only caller is the in-process host under test.
            Server = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
            Factory = new ShippingWorkerFactory(UnreachableSql, UnreachableRabbit, Server.Urls[0] + "/");
        }

        public WireMockServer Server { get; }

        public ShippingWorkerFactory Factory { get; }

        public void Dispose()
        {
            Factory.Dispose();
            Server.Stop();
        }
    }

    private readonly WireMockServer _server;
    private readonly ShippingWorkerFactory _factory;

    public HttpCarrierGatewayTests(CarrierHost host)
    {
        _server = host.Server;
        _factory = host.Factory;

        // Each test starts from the simulator's files alone: no stub another
        // test added, and no request it made.
        _server.ResetLogEntries();
        _server.ResetMappings();
        _server.ReadStaticMappings(SimulatorMappings.Directory());
    }

    private ICarrierGateway Carrier() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<ICarrierGateway>();

    private static BookingRequest Booking(string postalCode, ShipmentId? shipment = null) =>
        new(shipment ?? ShipmentId.New(), new DeliveryAddress("1 Abay Avenue", null, "Almaty", postalCode, "KZ"));

    private int Calls(string path) =>
        _server.LogEntries.Count(e => e.RequestMessage!.Path == path);

    [Fact]
    public async Task An_ordinary_address_books_and_the_key_is_the_shipments()
    {
        ShipmentId shipment = ShipmentId.New();
        CancellationToken ct = TestContext.Current.CancellationToken;

        BookingResult result = await Carrier().BookAsync(Booking("050000", shipment), ct);

        BookingResult.Booked booked = result.ShouldBeOfType<BookingResult.Booked>();
        booked.Reference.ShouldBe("crr_SIM-OK");
        booked.TrackingNumber.ShouldBe("TRK-SIM-OK");
        _server.LogEntries.ShouldAllBe(e =>
            e.RequestMessage!.Headers!["Idempotency-Key"].Single() == $"book:{shipment.Value}");
    }

    [Fact]
    public async Task The_carrier_is_shown_the_address_and_the_shipments_id_and_nothing_else()
    {
        ShipmentId shipment = ShipmentId.New();

        await Carrier().BookAsync(Booking("050000", shipment), TestContext.Current.CancellationToken);

        ILogEntry call = _server.LogEntries.ShouldHaveSingleItem();
        using JsonDocument body = JsonDocument.Parse(call.RequestMessage!.Body!);
        JsonElement root = body.RootElement;

        // The whole shape, not the absence of one word: a field added to the
        // body is a fact the carrier is shown, and section 7 names two.
        root.EnumerateObject().Select(p => p.Name).ShouldBe(["shipmentId", "address"], ignoreOrder: true);
        root.GetProperty("address").EnumerateObject().Select(p => p.Name).ShouldBe(
            ["line1", "line2", "city", "postalCode", "country"], ignoreOrder: true);
        root.GetProperty("shipmentId").GetGuid().ShouldBe(shipment.Value);
        root.GetProperty("address").GetProperty("line1").GetString().ShouldBe("1 Abay Avenue");
        call.RequestMessage.Headers!["Authorization"].Single()
            .ShouldBe($"Bearer {ShippingWorkerFactory.LocalCarrierApiKey}");
    }

    [Fact]
    public async Task A_refused_address_is_an_answer_and_is_not_retried()
    {
        BookingResult result = await Carrier().BookAsync(Booking("SIM-REFUSED"), TestContext.Current.CancellationToken);

        result.ShouldBe(new BookingResult.Refused("address_not_serviceable"));
        Calls("/v1/shipments").ShouldBe(1, "a refusal is an answer, and the pipeline does not retry a 422");
    }

    [Fact]
    public async Task A_refused_connection_is_unavailable_rather_than_a_refusal()
    {
        using ShippingWorkerFactory dead = new(UnreachableSql, UnreachableRabbit, "http://carrier.invalid/");

        await Should.ThrowAsync<CarrierUnavailableException>(() => dead.Services.CreateScope().ServiceProvider
            .GetRequiredService<ICarrierGateway>()
            .BookAsync(Booking("050000"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Every_attempt_and_every_bounded_delay_fit_inside_the_total()
    {
        TimeSpan worst = CarrierHop.AttemptTimeout * (CarrierHop.MaxRetryAttempts + 1)
                         + CarrierHop.MaxRetryDelay * CarrierHop.MaxRetryAttempts;

        worst.ShouldBeLessThan(CarrierHop.TotalRequestTimeout,
            "PricingHop's argument: a total that cancels the last retry makes the retry count a fiction");

        CarrierHop.TotalRequestTimeout.ShouldBeLessThan(Common.Web.ServiceOptions.OperationTimeout,
            "§9.7: the outbound client total must be strictly below the service operation total");
    }

    [Fact]
    public void The_attempt_timeout_is_outside_the_band_a_waiting_caller_is_sized_to()
    {
        // §9.7's 1-2 s band is a caller's patience. Nobody waits on this hop:
        // a worker's row backs off, so the attempt is sized to a third party
        // behind an anti-corruption layer (spec, section 9).
        CarrierHop.AttemptTimeout.ShouldBeGreaterThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_breaker_samples_over_at_least_two_attempt_timeouts()
    {
        CarrierHop.CircuitBreakerSamplingDuration.ShouldBeGreaterThanOrEqualTo(CarrierHop.AttemptTimeout * 2,
            "the library validates this pair at startup, and a host that will not start is not a budget");
    }

    [Fact]
    public async Task A_cancel_goes_to_the_references_path_under_the_cancel_key()
    {
        ShipmentId shipment = ShipmentId.New();

        CancellationResult result = await Carrier()
            .CancelAsync(new CancellationRequest(shipment, "crr_SIM-OK"), TestContext.Current.CancellationToken);

        result.ShouldBe(new CancellationResult.Cancelled());
        ILogEntry call = _server.LogEntries.ShouldHaveSingleItem();
        call.RequestMessage!.Path.ShouldBe("/v1/shipments/crr_SIM-OK/cancel");
        call.RequestMessage.Headers!["Idempotency-Key"].Single().ShouldBe($"cancel:{shipment.Value}");
    }

    [Fact]
    public async Task A_carrier_that_has_already_collected_the_parcel_answers_too_late()
    {
        CancellationResult result = await Carrier()
            .CancelAsync(
                new CancellationRequest(ShipmentId.New(), "crr_SIM-LATE"),
                TestContext.Current.CancellationToken);

        result.ShouldBe(new CancellationResult.TooLate());
        Calls("/v1/shipments/crr_SIM-LATE/cancel").ShouldBe(1, "section 9: too late is an answer, not a fault");
    }

    [Fact]
    public async Task A_feed_is_translated_and_the_carriers_link_is_nowhere_in_it()
    {
        IReadOnlyList<CarrierEvent> page = await Carrier()
            .GetEventsAsync("crr_SIM-OK", TestContext.Current.CancellationToken);

        page.Select(e => (e.CarrierEventId, e.Status)).ShouldBe(
        [
            ("evt-collected", TrackingStatus.Collected),
            ("evt-delivered", TrackingStatus.Delivered)
        ]);
        page.ShouldAllBe(e => e.OccurredAt.Year == 2026);
        string.Join(" ", page.Select(e => e.ToString())).ShouldNotContain("carrier.example");
    }

    [Fact]
    public async Task A_page_the_carrier_ordered_backwards_is_returned_as_sent()
    {
        IReadOnlyList<CarrierEvent> page = await Carrier()
            .GetEventsAsync("crr_SIM-REVERSED", TestContext.Current.CancellationToken);

        // The adapter sorts nothing: the key makes a repeated page free and
        // section 5's promotion is monotonic by rank, so an order imposed here
        // would hide the case the domain exists to survive.
        page.Select(e => e.Status).ShouldBe([TrackingStatus.Delivered, TrackingStatus.Collected]);
    }

    [Fact]
    public async Task A_status_nobody_agreed_is_unrecognised_and_moves_nothing()
    {
        _server.Given(Request.Create().WithPath("/v1/shipments/crr_x/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                "{\"events\":[{\"id\":\"e1\",\"status\":\"teleported\",\"occurredAt\":\"2026-01-02T09:00:00Z\"}]}"));

        IReadOnlyList<CarrierEvent> page = await Carrier()
            .GetEventsAsync("crr_x", TestContext.Current.CancellationToken);

        page.ShouldHaveSingleItem().Status.ShouldBe(TrackingStatus.Unrecognised,
            "a conformist that faults on a new status stops tracking every shipment until a deploy");
    }

    [Fact]
    public async Task A_hostile_page_is_the_carrier_being_wrong_and_nothing_of_it_is_returned()
    {
        // SIM-STRANGE's timestamp is decades ahead: a stored future instant
        // would sit ahead of every real one for ever, so the page is refused
        // whole rather than partly kept (spec, section 9).
        CarrierUnavailableException thrown = await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().GetEventsAsync("crr_SIM-STRANGE", TestContext.Current.CancellationToken));

        thrown.Message.ShouldNotContain("not-the-carrier.example");
    }

    [Fact]
    public async Task A_carrier_that_has_not_heard_of_the_booking_answers_an_empty_page()
    {
        _server.Given(Request.Create().WithPath("/v1/shipments/crr_new/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(404));

        (await Carrier().GetEventsAsync("crr_new", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        Calls("/v1/shipments/crr_new/events").ShouldBe(1, "a 404 is an answer, and the pipeline does not retry it");
    }

    [Theory]
    [InlineData(201, "{\"status\":\"refused\",\"reference\":\"crr_x\",\"trackingNumber\":\"t\"}")]
    [InlineData(201, "{\"status\":\"booked\",\"trackingNumber\":\"t\"}")]
    [InlineData(201, "{\"status\":\"booked\",\"reference\":\"crr_x\"}")]
    [InlineData(201, "{\"status\":\"booked\",\"reference\":\"   \",\"trackingNumber\":\"t\"}")]
    [InlineData(201, "not json")]
    [InlineData(422, "{\"status\":\"refused\"}")]
    [InlineData(422, "{\"status\":\"booked\",\"code\":\"address_not_serviceable\"}")]
    [InlineData(200, "{\"status\":\"booked\",\"reference\":\"crr_x\",\"trackingNumber\":\"t\"}")]
    public async Task A_booking_body_that_contradicts_its_status_is_unavailable_never_an_answer(int status, string body)
    {
        _server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("050000"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(200, "{\"status\":\"cancelled\",\"code\":null}", false)]
    [InlineData(200, "{\"status\":\"pending\"}", true)]
    [InlineData(202, "{\"status\":\"cancelled\"}", true)]
    [InlineData(409, "{\"status\":\"cancelled\"}", true)]
    public async Task A_cancel_answered_with_anything_else_is_the_carrier_being_wrong(
        int status, string body, bool throws)
    {
        _server.Given(Request.Create().WithPath("/v1/shipments/*/cancel").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        Func<Task<CancellationResult>> call = () => Carrier()
            .CancelAsync(new CancellationRequest(ShipmentId.New(), "crr_x"), TestContext.Current.CancellationToken);

        if (throws)
            await Should.ThrowAsync<CarrierUnavailableException>(call);
        else
            (await call()).ShouldBe(new CancellationResult.Cancelled());
    }

    [Theory]
    [InlineData(CarrierLimits.MaxReferenceLength, true)]
    [InlineData(CarrierLimits.MaxReferenceLength + 1, false)]
    public async Task A_reference_longer_than_the_column_is_refused_before_it_is_recorded(int length, bool accepted)
    {
        string reference = new('r', length);
        _server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201).WithBody(
                $"{{\"status\":\"booked\",\"reference\":\"{reference}\",\"trackingNumber\":\"t\"}}"));

        Func<Task<BookingResult>> call = () => Carrier()
            .BookAsync(Booking("050000"), TestContext.Current.CancellationToken);

        if (accepted)
            (await call()).ShouldBe(new BookingResult.Booked(reference, "t"));
        else
            await Should.ThrowAsync<CarrierUnavailableException>(call);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task A_reference_that_is_a_dot_segment_is_refused_before_it_is_recorded(string reference)
    {
        // Kept, it would be spliced into the cancel and events paths, where a
        // dot segment is resolved away and the call reaches another endpoint.
        _server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201).WithBody(
                $"{{\"status\":\"booked\",\"reference\":\"{reference}\",\"trackingNumber\":\"t\"}}"));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("050000"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(CarrierLimits.MaxTrackingNumberLength, true)]
    [InlineData(CarrierLimits.MaxTrackingNumberLength + 1, false)]
    public async Task A_tracking_number_longer_than_the_column_is_refused_before_it_is_recorded(
        int length, bool accepted)
    {
        string tracking = new('t', length);
        _server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201).WithBody(
                $"{{\"status\":\"booked\",\"reference\":\"crr_x\",\"trackingNumber\":\"{tracking}\"}}"));

        Func<Task<BookingResult>> call = () => Carrier()
            .BookAsync(Booking("050000"), TestContext.Current.CancellationToken);

        if (accepted)
            (await call()).ShouldBe(new BookingResult.Booked("crr_x", tracking));
        else
            await Should.ThrowAsync<CarrierUnavailableException>(call);
    }

    [Theory]
    [InlineData(CarrierLimits.MaxReasonLength, true)]
    [InlineData(CarrierLimits.MaxReasonLength + 1, false)]
    public async Task A_refusal_code_longer_than_the_column_is_refused_before_it_is_recorded(int length, bool accepted)
    {
        string code = new('c', length);
        _server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(422).WithBody(
                $"{{\"status\":\"refused\",\"code\":\"{code}\"}}"));

        Func<Task<BookingResult>> call = () => Carrier()
            .BookAsync(Booking("050000"), TestContext.Current.CancellationToken);

        if (accepted)
            (await call()).ShouldBe(new BookingResult.Refused(code));
        else
            await Should.ThrowAsync<CarrierUnavailableException>(call);
    }

    [Fact]
    public async Task A_body_in_a_charset_nobody_can_decode_is_unavailable_and_counted()
    {
        _server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201)
                .WithHeader("Content-Type", "application/json; charset=bogus")
                .WithBody("{\"status\":\"booked\",\"reference\":\"crr_x\",\"trackingNumber\":\"t\"}"));
        using UnavailableCount counted = UnavailableCounter.Of(_factory.Services);

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("050000"), TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(1, "one attempt, answered with a body this adapter cannot read");
    }

    [Fact]
    public async Task An_event_id_longer_than_its_key_is_refused_with_the_page()
    {
        string id = new('e', CarrierLimits.MaxCarrierEventIdLength + 1);
        string page =
            $"{{\"events\":[{{\"id\":\"{id}\",\"status\":\"collected\",\"occurredAt\":\"2026-01-02T09:00:00Z\"}}]}}";
        _server.Given(Request.Create().WithPath("/v1/shipments/crr_x/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(page));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().GetEventsAsync("crr_x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_page_longer_than_the_bound_is_refused()
    {
        string events = string.Join(",", Enumerable.Range(0, CarrierHop.MaxEventsPerPage + 1).Select(i =>
            $"{{\"id\":\"e{i}\",\"status\":\"in_transit\",\"occurredAt\":\"2026-01-02T09:00:00Z\"}}"));
        _server.Given(Request.Create().WithPath("/v1/shipments/crr_x/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($"{{\"events\":[{events}]}}"));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().GetEventsAsync("crr_x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_counted_against_the_carrier()
    {
        using UnavailableCount counted = UnavailableCounter.Of(_factory.Services);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Carrier().BookAsync(Booking("050000"), cancelled.Token));

        counted.Value.ShouldBe(0, "a pass cancelled at shutdown is not a carrier incident");
    }

    [Theory]
    [InlineData("http://carrier.example/", false)]
    [InlineData("https://carrier.example/", true)]
    public void Outside_development_only_an_https_carrier_is_accepted(string address, bool starts)
    {
        using ShippingWorkerFactory factory = new(UnreachableSql, UnreachableRabbit, address);
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        if (starts)
            production.Services.GetRequiredService<ICarrierGateway>().ShouldNotBeNull();
        else
        {
            Should.Throw<InvalidOperationException>(() => production.Services)
                .Message.ShouldContain("plain HTTP outside Development");
        }
    }

    [Fact]
    public void A_missing_base_url_stops_the_host()
    {
        using ShippingWorkerFactory factory = new(UnreachableSql, UnreachableRabbit, carrierBaseUrl: "");

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain(CarrierRegistration.BaseUrlKey);
    }

    [Fact]
    public void A_missing_carrier_key_stops_the_host()
    {
        using ShippingWorkerFactory factory = new(
            UnreachableSql, UnreachableRabbit, "https://carrier.example/", carrierApiKey: " ");

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain(CarrierRegistration.ApiKeyKey, Case.Sensitive,
                "§15.4 marks the key required; a host must not call a carrier unauthenticated");
    }
}
