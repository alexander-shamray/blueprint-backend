using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application;
using Payments.Application.Provider;
using Payments.Domain.Orders;
using Payments.Infrastructure.Provider;
using Payments.TestSupport;
using Shouldly;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;
using ProviderRegistration = Payments.Infrastructure.Provider.DependencyInjection;

namespace Payments.Api.Tests;

/// <summary>
/// The adapter over a real HTTP server loading the simulator's own mappings,
/// so the file Compose runs is the file these assert (§12: WireMock.Net for a
/// third-party API).
/// </summary>
public sealed class HttpPaymentProviderTests : IClassFixture<HttpPaymentProviderTests.ProviderHost>
{
    private const string UnreachableSql =
        "Server=sql.invalid;Database=Payments;User Id=x;Password=x;TrustServerCertificate=true";

    private const string UnreachableRabbit = "amqp://payments-svc:x@rabbit.invalid:5672";

    /// <summary>
    /// One server and one host for the class: a host over an unreachable
    /// broker can take seconds to stop, so only a test that needs different
    /// settings builds its own.
    /// </summary>
    public sealed class ProviderHost : IDisposable
    {
        public ProviderHost()
        {
            Server = WireMockServer.Start();
            Factory = new PaymentsApiFactory(UnreachableSql, UnreachableRabbit, Server.Urls[0] + "/");
        }

        public WireMockServer Server { get; }

        public PaymentsApiFactory Factory { get; }

        public void Dispose()
        {
            Factory.Dispose();
            Server.Stop();
        }
    }

    private readonly WireMockServer _server;
    private readonly PaymentsApiFactory _factory;

    public HttpPaymentProviderTests(ProviderHost host)
    {
        _server = host.Server;
        _factory = host.Factory;

        // Each test starts from the simulator's files alone: no stub another
        // test added, and no request it made.
        _server.ResetLogEntries();
        _server.ResetMappings();
        _server.ReadStaticMappings(SimulatorMappings.Directory());
    }

    private IPaymentProvider Provider() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<IPaymentProvider>();

    private static AuthorisationRequest Authorisation(decimal amount, OrderId? order = null) =>
        new(order ?? OrderId.New(), Guid.CreateVersion7(), amount, "EUR");

    private int Calls(string path) =>
        _server.LogEntries.Count(e => e.RequestMessage!.Path == path);

    // This host's meter, never one matched by name: a MeterListener is
    // process-wide, and another host's provider would count into it. The
    // factory caches by name, so this is the instance ProviderMetrics holds,
    // and resolving ProviderMetrics first means the counter already exists.
    private UnavailableCount CountUnavailable() => CountUnavailable(_factory);

    private static UnavailableCount CountUnavailable(PaymentsApiFactory factory)
    {
        factory.Services.GetRequiredService<ProviderMetrics>();
        Meter mine = factory.Services.GetRequiredService<IMeterFactory>().Create(ProviderMetrics.MeterName);
        UnavailableCount count = new(mine);
        count.Enabled.ShouldBeTrue("no counter on this host's meter was enabled, so a zero would prove nothing");
        return count;
    }

    private sealed class UnavailableCount : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _counted;

        public UnavailableCount(Meter mine)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, mine) && instrument.Name == "payments.provider.unavailable")
                {
                    l.EnableMeasurementEvents(instrument);
                    Enabled = true;
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _counted, value));
            _listener.Start();
        }

        public bool Enabled { get; private set; }

        public long Value => Interlocked.Read(ref _counted);

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task An_ordinary_amount_is_authorised_and_a_replay_of_the_key_answers_the_same_reference()
    {
        OrderId order = OrderId.New();
        CancellationToken ct = TestContext.Current.CancellationToken;

        AuthorisationResult first = await Provider().AuthoriseAsync(Authorisation(42.10m, order), ct);
        AuthorisationResult second = await Provider().AuthoriseAsync(Authorisation(42.10m, order), ct);

        AuthorisationResult.Authorised authorised = first.ShouldBeOfType<AuthorisationResult.Authorised>();
        authorised.Reference.ShouldBe($"psp_authorise:{order.Value}");
        second.ShouldBe(first, "section 4: a unit retried after the provider answered receives the same answer");
        _server.LogEntries.ShouldAllBe(e =>
            e.RequestMessage!.Headers!["Idempotency-Key"].Single() == $"authorise:{order.Value}");

        // The simulator ignores the credential, so only this line fails if the
        // adapter stops sending it: compared with what the host configured, so
        // the test prints no key of its own.
        string configured = _factory.Services.GetRequiredService<IConfiguration>()[ProviderRegistration.ApiKeyKey]!;
        _server.LogEntries.ShouldAllBe(e =>
            e.RequestMessage!.Headers!["Authorization"].Single() == $"Bearer {configured}");
    }

    [Fact]
    public async Task The_authorisation_body_carries_the_minor_amount_the_currency_and_the_payer_and_nothing_else()
    {
        // The simulator matches on the amount alone, so only this reads the
        // other two fields of the wire format off the request the adapter sent.
        Guid payer = Guid.CreateVersion7();

        await Provider().AuthoriseAsync(
            new AuthorisationRequest(OrderId.New(), payer, 42.10m, "EUR"), TestContext.Current.CancellationToken);

        using JsonDocument sent = JsonDocument.Parse(_server.LogEntries.ShouldHaveSingleItem().RequestMessage!.Body!);
        sent.RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(["amountMinor", "currency", "payerId"], ignoreOrder: true);
        sent.RootElement.GetProperty("amountMinor").GetInt64().ShouldBe(4210);
        sent.RootElement.GetProperty("currency").GetString().ShouldBe("EUR");
        sent.RootElement.GetProperty("payerId").GetGuid().ShouldBe(payer);
    }

    [Theory]
    [InlineData(10.01, "card_declined")]
    [InlineData(0.01, "card_declined")]
    [InlineData(10.02, "insufficient_funds")]
    [InlineData(0.02, "insufficient_funds")]
    public async Task A_scripted_decline_is_a_decline_with_the_providers_code(decimal amount, string code)
    {
        AuthorisationResult result =
            await Provider().AuthoriseAsync(Authorisation(amount), TestContext.Current.CancellationToken);

        result.ShouldBe(new AuthorisationResult.Declined(code));
        Calls("/v1/authorisations").ShouldBe(1, "a decline is an answer, and the pipeline does not retry a 402");
    }

    [Fact]
    public async Task A_503_is_retried_in_the_client_then_thrown_as_unavailable_and_counted_per_attempt()
    {
        using UnavailableCount counted = CountUnavailable();

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(10.05m), TestContext.Current.CancellationToken));

        Calls("/v1/authorisations").ShouldBe(ProviderHop.MaxRetryAttempts + 1);
        counted.Value.ShouldBe(ProviderHop.MaxRetryAttempts + 1, "one per failing attempt, not one per call");
    }

    [Fact]
    public async Task A_long_retry_after_does_not_spend_the_budget_the_retries_are_owed()
    {
        // A provider's Retry-After replaces the bounded backoff, and nothing
        // caps it at ProviderHop.MaxRetryDelay; honoured, one long header
        // spends the total before the retries the budget test counts on.
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(503).WithHeader("Retry-After", "60"));

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));

        Calls("/v1/authorisations").ShouldBe(ProviderHop.MaxRetryAttempts + 1);
    }

    [Fact]
    public async Task A_refused_connection_is_retried_then_thrown_as_unavailable_and_counted_per_attempt()
    {
        // A refused connection reaches the client as an HttpRequestException,
        // counted by the attempt handler's own branch rather than by status.
        // The in-process server answers its connection faults with a status,
        // so the provider here is a port nothing listens on.
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using PaymentsApiFactory factory = new(UnreachableSql, UnreachableRabbit, $"http://127.0.0.1:{closed}/");
        using UnavailableCount counted = CountUnavailable(factory);

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            factory.Services.CreateScope().ServiceProvider.GetRequiredService<IPaymentProvider>()
                .AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(ProviderHop.MaxRetryAttempts + 1, "one per refused attempt, not one per call");
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    public async Task A_timeout_or_throttle_status_is_retried_then_thrown_as_unavailable(int status)
    {
        // Stubbed rather than scripted: the translation table names both, and
        // the simulator scripts neither, so without this a branch that dropped
        // either would leave the suite green.
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status));

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));

        Calls("/v1/authorisations")
            .ShouldBe(ProviderHop.MaxRetryAttempts + 1, "the pipeline retries both, as it does a 503");
    }

    [Theory]
    [InlineData(1001, 402)]
    [InlineData(1002, 402)]
    [InlineData(1005, 503)]
    [InlineData(1011, 201)]
    public async Task The_simulator_scripts_an_amount_however_its_json_is_spaced(long amountMinor, int expected)
    {
        // Straight at the simulator, past the adapter: a person probing it by
        // hand sends spaced JSON, and a scripted amount must not fall through
        // to an approval because of it.
        using HttpClient client = new() { BaseAddress = new Uri(_server.Urls[0]) };
        using StringContent body = new(
            $"{{ \"amountMinor\" : {amountMinor} , \"currency\" : \"EUR\" }}",
            System.Text.Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response =
            await client.PostAsync("/v1/authorisations", body, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0.11)]
    [InlineData(0.21)]
    public async Task An_amount_ending_in_one_that_is_not_one_cent_is_approved(decimal amount)
    {
        (await Provider().AuthoriseAsync(Authorisation(amount), TestContext.Current.CancellationToken))
            .ShouldBeOfType<AuthorisationResult.Authorised>("only a minor amount ending 01, or exactly 1, is scripted");
    }

    [Fact]
    public async Task A_stalled_provider_is_unavailable_within_the_total_budget_and_its_timeouts_count()
    {
        using UnavailableCount counted = CountUnavailable();
        DateTimeOffset started = DateTimeOffset.UtcNow;

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(10.09m), TestContext.Current.CancellationToken));

        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(ProviderHop.TotalRequestTimeout + TimeSpan.FromSeconds(2));
        counted.Value.ShouldBe(
            ProviderHop.MaxRetryAttempts + 1, "every attempt timed out, each the provider's, counted by OnTimeout");
    }

    [Fact]
    public void Every_attempt_and_every_bounded_delay_fit_inside_the_total()
    {
        TimeSpan worst = ProviderHop.AttemptTimeout * (ProviderHop.MaxRetryAttempts + 1)
                         + ProviderHop.MaxRetryDelay * ProviderHop.MaxRetryAttempts;

        worst.ShouldBeLessThan(ProviderHop.TotalRequestTimeout,
            "the Web.Bff's PricingHop argument: a total that cancels the last retry makes the retry count a fiction");
    }

    [Fact]
    public async Task A_409_is_a_mismatch_and_is_not_retried()
    {
        // Stubbed here, not in the mappings: a stateless simulator cannot know a
        // key was used before (spec, section 9).
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(409));

        await Should.ThrowAsync<PaymentMismatchException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));
        Calls("/v1/authorisations").ShouldBe(1);
    }

    [Theory]
    [InlineData(202, "{\"status\":\"voided\"}")]
    [InlineData(204, "")]
    [InlineData(200, "{\"status\":\"pending\"}")]
    [InlineData(200, "not json")]
    public async Task A_void_answered_with_anything_but_200_voided_is_not_a_void(int status, string body)
    {
        _server.Given(Request.Create().WithPath("/v1/authorisations/*/void").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));
        using UnavailableCount counted = CountUnavailable();

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().VoidAsync(new VoidRequest(OrderId.New(), "psp_ref"), TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(1, "one attempt, answered with something that is not a void");
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_redirect_is_not_followed_and_is_unavailable(int status)
    {
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Location", "/elsewhere"));
        _server.Given(Request.Create().WithPath("/elsewhere").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201)
                .WithBody("{\"status\":\"approved\",\"reference\":\"psp_elsewhere\"}"));

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));

        Calls("/elsewhere").ShouldBe(0, "the payer and the amount go to the provider's address and nowhere else");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task A_status_the_wire_format_does_not_define_is_unavailable_and_counted_once(int status)
    {
        // A 401 is a wrong key: not retried, and not a verdict either, so it is
        // exactly the failing provider the counter exists to show first.
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status));
        using UnavailableCount counted = CountUnavailable();

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));

        Calls("/v1/authorisations").ShouldBe(1);
        counted.Value.ShouldBe(1);
    }

    [Fact]
    public async Task A_void_goes_to_the_references_path_under_the_void_key()
    {
        OrderId order = OrderId.New();

        await Provider().VoidAsync(new VoidRequest(order, "psp_ref"), TestContext.Current.CancellationToken);

        ILogEntry call = _server.LogEntries.ShouldHaveSingleItem();
        call.RequestMessage!.Path.ShouldBe("/v1/authorisations/psp_ref/void");
        call.RequestMessage.Headers!["Idempotency-Key"].Single().ShouldBe($"void:{order.Value}");
    }

    [Fact]
    public async Task A_base_url_with_a_path_and_no_trailing_slash_keeps_its_path()
    {
        using PaymentsApiFactory factory = new(UnreachableSql, UnreachableRabbit, _server.Urls[0] + "/psp");
        _server.Given(Request.Create().WithPath("/psp/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201)
                .WithBody("{\"status\":\"approved\",\"reference\":\"psp_p\"}"));

        AuthorisationResult result = await factory.Services.CreateScope().ServiceProvider
            .GetRequiredService<IPaymentProvider>()
            .AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken);

        result.ShouldBe(new AuthorisationResult.Authorised("psp_p"));
    }

    [Theory]
    [InlineData("http://psp.example/", false)]
    [InlineData("https://psp.example/", true)]
    public void Outside_development_only_an_https_provider_is_accepted(string address, bool starts)
    {
        using PaymentsApiFactory factory = new(UnreachableSql, UnreachableRabbit, address);
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        if (starts)
        {
            production.Services.GetRequiredService<IPaymentProvider>().ShouldNotBeNull();
        }
        else
        {
            Should.Throw<InvalidOperationException>(() => production.Services)
                .Message.ShouldContain("plain HTTP outside Development");
        }
    }

    [Fact]
    public void A_missing_base_url_stops_the_host()
    {
        using PaymentsApiFactory factory = new(UnreachableSql, UnreachableRabbit, providerBaseUrl: "");

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain("PaymentProvider:BaseUrl");
    }

    [Theory]
    [InlineData("https://payer:not-the-key@psp.example/")]
    [InlineData("ftp://payer:not-the-key@psp.example/")]
    public void A_base_url_carrying_user_information_is_refused_without_echoing_it(string address)
    {
        using PaymentsApiFactory factory = new(UnreachableSql, UnreachableRabbit, address);

        string message = Should.Throw<InvalidOperationException>(() => factory.Services).Message;

        message.ShouldContain("PaymentProvider:BaseUrl");
        message.ShouldNotContain("not-the-key", Case.Insensitive, "a startup message is logged; a credential is not");
    }

    [Theory]
    [InlineData("https://psp.example/api?tenant=private-tenant")]
    [InlineData("https://psp.example/api#private-tenant")]
    public void A_base_url_with_a_query_or_fragment_is_refused_without_echoing_it(string address)
    {
        using PaymentsApiFactory factory = new(UnreachableSql, UnreachableRabbit, address);

        string message = Should.Throw<InvalidOperationException>(() => factory.Services).Message;

        message.ShouldContain("PaymentProvider:BaseUrl");
        message.ShouldNotContain("private-tenant", Case.Insensitive, "a query can carry what a log must not");
    }

    [Fact]
    public void A_missing_provider_key_stops_the_host()
    {
        using PaymentsApiFactory factory = new(
            UnreachableSql, UnreachableRabbit, _server.Urls[0] + "/", providerApiKey: " ");

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain("PaymentProvider:ApiKey", Case.Sensitive,
                "§15.4 marks the key required; a host must not call a provider unauthenticated");
    }

    [Theory]
    [InlineData(201, "{\"status\":\"declined\",\"reference\":\"psp_x\"}")]
    [InlineData(201, "{\"status\":\"approved\"}")]
    [InlineData(402, "{\"status\":\"declined\"}")]
    [InlineData(402, "{\"status\":\"approved\",\"code\":\"card_declined\"}")]
    [InlineData(201, "not json")]
    [InlineData(201, "{\"status\":\"approved\",\"reference\":\"   \"}")]
    [InlineData(402, "{\"status\":\"declined\",\"code\":\" \"}")]
    public async Task A_body_that_contradicts_its_status_is_unavailable_never_a_verdict(int status, string body)
    {
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));
        using UnavailableCount counted = CountUnavailable();

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(1, "one attempt, answered with something that is not a verdict");
    }

    [Theory]
    [InlineData(ProviderLimits.MaxReferenceLength, true)]
    [InlineData(ProviderLimits.MaxReferenceLength + 1, false)]
    public async Task A_reference_longer_than_the_column_is_refused_before_it_is_recorded(int length, bool accepted)
    {
        string reference = new('r', length);
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201)
                .WithBody($"{{\"status\":\"approved\",\"reference\":\"{reference}\"}}"));

        Func<Task<AuthorisationResult>> call = () =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken);

        if (accepted)
            (await call()).ShouldBe(new AuthorisationResult.Authorised(reference));
        else
            await Should.ThrowAsync<PaymentProviderUnavailableException>(call);
    }

    [Theory]
    [InlineData(ProviderLimits.MaxReasonLength, true)]
    [InlineData(ProviderLimits.MaxReasonLength + 1, false)]
    public async Task A_decline_code_longer_than_the_column_is_refused_before_it_is_recorded(int length, bool accepted)
    {
        string code = new('c', length);
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(402)
                .WithBody($"{{\"status\":\"declined\",\"code\":\"{code}\"}}"));

        Func<Task<AuthorisationResult>> call = () =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken);

        if (accepted)
            (await call()).ShouldBe(new AuthorisationResult.Declined(code));
        else
            await Should.ThrowAsync<PaymentProviderUnavailableException>(call);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_counted_against_the_provider()
    {
        using UnavailableCount counted = CountUnavailable();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), cancelled.Token));

        counted.Value.ShouldBe(0, "a consume cancelled at shutdown is not a provider incident");
    }

    [Fact]
    public async Task A_cancellation_during_an_attempt_is_the_callers_and_is_not_counted()
    {
        using UnavailableCount counted = CountUnavailable();
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancelled.CancelAfter(TimeSpan.FromSeconds(1));

        // The stalled script, so the cancellation lands inside an attempt,
        // where it and an attempt timeout arrive as the same exception.
        await Should.ThrowAsync<OperationCanceledException>(() =>
            Provider().AuthoriseAsync(Authorisation(10.09m), cancelled.Token));

        counted.Value.ShouldBe(0, "the caller cancelling mid-attempt is not a provider incident");
    }
}
