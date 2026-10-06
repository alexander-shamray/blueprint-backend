using System.Diagnostics.Metrics;
using Common.Infrastructure.Identity;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Addresses;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Fulfilment;
using Shipping.OrderingStub;
using Shipping.TestSupport;
using Shouldly;
using Xunit;
using AddressRegistration = Shipping.Infrastructure.Addresses.DependencyInjection;

namespace Shipping.Worker.Tests;

/// <summary>ADR-052's five outcomes, read from the client's side, over a real gRPC server on loopback.</summary>
public sealed class DeliveryAddressSourceTests : IClassFixture<DeliveryAddressSourceTests.OrderingHost>
{
    /// <summary>One stub and one host for the class, as a host over an unreachable broker is slow to stop.</summary>
    /// <remarks>One transport fault stays under <see cref="AddressHop.CircuitBreakerMinimumThroughput"/>.</remarks>
    public sealed class OrderingHost : IAsyncLifetime
    {
        public StubOrdering Ordering { get; } = new();

        public ShippingWorkerFactory Factory { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            await Ordering.InitializeAsync();
            Factory = new ShippingWorkerFactory(
                Unreachable.Sql,
                Unreachable.Rabbit,
                addressSourceBaseUrl: Ordering.Address.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            Factory.Dispose();
            await Ordering.DisposeAsync();
        }
    }

    private readonly StubOrdering _ordering;
    private readonly ShippingWorkerFactory _factory;

    public DeliveryAddressSourceTests(OrderingHost host)
    {
        _ordering = host.Ordering;
        _factory = host.Factory;
        _ordering.Reset();
    }

    private IDeliveryAddressSource Source() => Source(_factory);

    private static IDeliveryAddressSource Source(WebApplicationFactory<Program> host) =>
        host.Services.CreateScope().ServiceProvider.GetRequiredService<IDeliveryAddressSource>();

    private OutboundCount CountRefused() => OutboundCounter.Refused(_factory.Services);

    private Guid KnownOrder(string postalCode = "050000")
    {
        Guid order = Guid.CreateVersion7();
        _ordering.Addresses[order] =
            new StubAddress(Guid.CreateVersion7(), "1 Abay Avenue", null, "Almaty", postalCode, "KZ");

        return order;
    }

    [Fact]
    public async Task An_order_answers_with_its_address_and_its_customer()
    {
        Guid order = KnownOrder();
        StubAddress expected = _ordering.Addresses[order];

        AddressLookup lookup = await Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken);

        AddressLookup.Found found = lookup.ShouldBeOfType<AddressLookup.Found>();
        found.CustomerId.ShouldBe(expected.CustomerId, "the row erasure deletes by is carried on the reply (ADR-052)");
        found.Address.ShouldBe(new DeliveryAddress("1 Abay Avenue", null, "Almaty", "050000", "KZ"));
    }

    [Fact]
    public async Task An_empty_second_line_arrives_as_absent_rather_than_blank()
    {
        Guid order = Guid.CreateVersion7();
        _ordering.Addresses[order] =
            new StubAddress(Guid.CreateVersion7(), "1 Abay Avenue", "", "Almaty", "050000", "KZ");

        AddressLookup lookup = await Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken);

        lookup.ShouldBeOfType<AddressLookup.Found>().Address.Line2.ShouldBeNull(
            "proto3 has no null string, so the absence arrives as \"\" and is stored as the absence it is");
    }

    [Fact]
    public async Task The_call_carries_the_hosts_own_token()
    {
        await Source().GetAsync(new OrderId(KnownOrder()), TestContext.Current.CancellationToken);

        _ordering.Tokens.ShouldHaveSingleItem().ShouldStartWith("Bearer ");
    }

    [Fact]
    public async Task NotFound_is_the_one_answer_that_means_the_order_has_no_address()
    {
        AddressLookup lookup = await Source().GetAsync(
            new OrderId(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        lookup.ShouldBeOfType<AddressLookup.NoSuchOrder>();
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task A_refused_credential_is_counted_and_thrown_rather_than_treated_as_an_absence(StatusCode status)
    {
        using OutboundCount counted = CountRefused();
        _ordering.Fail(status);

        await Should.ThrowAsync<AddressSourceRefusedException>(() =>
            Source().GetAsync(new OrderId(KnownOrder()), TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(1, "a revoked grant is a defect somebody must see, not an outage to wait out");
    }

    [Fact]
    public async Task A_refusal_decided_inside_the_pipeline_reaches_the_caller_as_itself_and_is_counted_once()
    {
        // The grant check runs in the client's own handler chain, where
        // Grpc.Net.Client reports any exception as a status of its own; this
        // is the whole host, so that translation is the one under test.
        using WebApplicationFactory<Program> refusing = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITokenCache>();
                services.AddSingleton<ITokenCache>(sp =>
                    new GrantCheckedTokenCache(
                        new RefusingTokenCache(),
                        sp.GetRequiredService<AddressMetrics>(),
                        NullLogger<GrantCheckedTokenCache>.Instance));
            }));
        using OutboundCount counted = OutboundCounter.Refused(refusing.Services);

        await Should.ThrowAsync<AddressSourceRefusedException>(() =>
            Source(refusing).GetAsync(new OrderId(KnownOrder()), TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(1, "counted where the refusal was decided, and not again on the way out");
        _ordering.Calls.ShouldBeEmpty("a host whose own token was refused sends nothing");
    }

    [Fact]
    public void The_host_draws_its_token_through_the_grant_check()
    {
        using ProgramTokensFactory host = new();

        host.Services.GetRequiredService<ITokenCache>().ShouldBeOfType<GrantCheckedTokenCache>();
    }

    [Fact]
    public async Task A_transient_status_is_thrown_uncounted_after_exactly_one_call()
    {
        using OutboundCount counted = CountRefused();
        Guid order = KnownOrder();
        _ordering.Fail(StatusCode.Unavailable, StatusCode.Unavailable, StatusCode.Unavailable);

        await Should.ThrowAsync<RpcException>(() =>
            Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(0, "an outage is not a decision anybody took");

        // One, not three: a gRPC status rides an HTTP 200, which the resilience handler hands straight back.
        _ordering.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_transport_fault_is_retried_inside_the_budget_and_the_call_recovers()
    {
        Guid order = KnownOrder();

        // An aborted connection, the shape an owner that is down produces, and one AddressHop's retry covers.
        _ordering.AbortNextCalls = 1;

        AddressLookup lookup = await Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken);

        lookup.ShouldBeOfType<AddressLookup.Found>();
        _ordering.Calls.Count.ShouldBe(2, "the pipeline retried inside AddressHop's budget");

        // The credential handler inside the pipeline runs once per attempt
        // (§11.5); registered outside it, both attempts would carry one token.
        _ordering.Tokens.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task An_unreachable_owner_throws_rather_than_answering()
    {
        using ShippingWorkerFactory dead = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            addressSourceBaseUrl: ShippingWorkerFactory.UnreachableAddressSource);

        await Should.ThrowAsync<RpcException>(() =>
            Source(dead).GetAsync(new OrderId(Guid.CreateVersion7()), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("line1")]
    [InlineData("line2")]
    [InlineData("city")]
    [InlineData("post_code")]
    [InlineData("country")]
    public async Task A_field_wider_than_its_column_is_refused_by_name_and_never_by_value(string field)
    {
        using OutboundCount counted = CountRefused();
        Guid order = Guid.CreateVersion7();
        _ordering.Addresses[order] = Widened(field);

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(field);

        // The value is an address, and a message is what a log carries.
        thrown.Message.ShouldNotContain("ЖЖ");
        counted.Value.ShouldBe(0, "a malformed answer is not a refused credential");
    }

    [Fact]
    public async Task Every_field_at_its_column_width_is_accepted()
    {
        Guid order = Guid.CreateVersion7();
        StubAddress widest = new(
            Guid.CreateVersion7(),
            new string('Ж', AddressLimits.MaxLineLength),
            new string('Ж', AddressLimits.MaxLineLength),
            new string('Ж', AddressLimits.MaxCityLength),
            new string('Ж', AddressLimits.MaxPostalCodeLength),
            "KZ");
        _ordering.Addresses[order] = widest;

        AddressLookup lookup = await Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken);

        lookup.ShouldBeOfType<AddressLookup.Found>().Address.ShouldBe(
            new DeliveryAddress(widest.Line1, widest.Line2, widest.City, widest.PostalCode, widest.Country));
    }

    [Theory]
    [InlineData("K")]
    [InlineData("1Z")]
    [InlineData("kz")]
    [InlineData("Kz")]
    public async Task A_country_that_is_not_two_upper_case_letters_is_refused(string country)
    {
        Guid order = Guid.CreateVersion7();
        _ordering.Addresses[order] =
            new StubAddress(Guid.CreateVersion7(), "1 Abay Avenue", null, "Almaty", "050000", country);

        (await Should.ThrowAsync<InvalidOperationException>(() =>
                Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken)))
            .Message.ShouldContain("country");
    }

    [Theory]
    [InlineData("line1")]
    [InlineData("city")]
    [InlineData("post_code")]
    public async Task An_empty_required_field_is_refused_by_name(string field)
    {
        StubAddress valid = new(Guid.CreateVersion7(), "1 Abay Avenue", null, "Almaty", "050000", "KZ");
        Guid order = Guid.CreateVersion7();
        _ordering.Addresses[order] = field switch
        {
            "line1" => valid with { Line1 = "" },
            "city" => valid with { City = "" },
            "post_code" => valid with { PostalCode = "" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "not a required field of the reply")
        };

        (await Should.ThrowAsync<InvalidOperationException>(() =>
                Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken)))
            .Message.ShouldBe($"Ordering answered with an empty {field}.");
    }

    [Fact]
    public async Task An_empty_customer_is_refused_rather_than_stored_under_no_subject()
    {
        Guid order = Guid.CreateVersion7();
        _ordering.Addresses[order] =
            new StubAddress(Guid.Empty, "1 Abay Avenue", null, "Almaty", "050000", "KZ");

        (await Should.ThrowAsync<InvalidOperationException>(() =>
                Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken)))
            .Message.ShouldContain("customer_id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ordering-api/")]
    [InlineData("ftp://ordering.example/")]
    public void An_address_source_that_is_not_an_absolute_http_address_stops_the_host(string configured)
    {
        using ShippingWorkerFactory factory = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            addressSourceBaseUrl: configured);

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain(AddressRegistration.BaseUrlKey);
    }

    [Theory]
    [InlineData("http://ordering.example/?region=kz")]
    [InlineData("http://ordering.example/#delivery")]
    public void An_address_source_with_a_query_or_fragment_stops_the_host(string configured)
    {
        using ShippingWorkerFactory factory = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            addressSourceBaseUrl: configured);

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldBe(
                $"{AddressRegistration.BaseUrlKey} carries a query or fragment, " +
                "which no request to Ordering would keep.");
    }

    [Fact]
    public void An_address_source_carrying_user_information_stops_the_host_without_echoing_it()
    {
        using ShippingWorkerFactory factory = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            addressSourceBaseUrl: "http://shipping:hunter2@ordering.example/");

        string message = Should.Throw<InvalidOperationException>(() => factory.Services).Message;

        message.ShouldContain("user information");
        message.ShouldNotContain("hunter2");
    }

    [Fact]
    public void The_built_pipeline_fits_every_attempt_and_every_bounded_delay_inside_the_total()
    {
        // Off the built host, by the name the handler registered under, so this
        // checks the registration rather than restating the constants (§9.7).
        HttpStandardResilienceOptions options = _factory.Services
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(AddressHop.ResilienceOptionsName);

        options.AttemptTimeout.Timeout.ShouldBe(AddressHop.AttemptTimeout);
        options.Retry.MaxRetryAttempts.ShouldBe(AddressHop.MaxRetryAttempts);
        options.Retry.MaxDelay.ShouldBe(AddressHop.MaxRetryDelay, "jitter makes the nominal delay no bound");
        options.TotalRequestTimeout.Timeout.ShouldBe(AddressHop.TotalRequestTimeout);

        TimeSpan worst = options.AttemptTimeout.Timeout * (options.Retry.MaxRetryAttempts + 1) +
            options.Retry.MaxDelay!.Value * options.Retry.MaxRetryAttempts;

        worst.ShouldBeLessThan(
            options.TotalRequestTimeout.Timeout,
            "a total that cancels the last retry makes the retry count a fiction");

        options.TotalRequestTimeout.Timeout.ShouldBeLessThan(Common.Web.ServiceOptions.OperationTimeout);

        // §9.7's bands, since Ordering is a peer, where CarrierHop, sized to a third party, sits outside them.
        options.AttemptTimeout.Timeout.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        options.TotalRequestTimeout.Timeout.ShouldBeInRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_lease_outlives_the_longest_pass_a_row_can_take()
    {
        // The worst pass is three calls: the address read, the booking, and the cancel a refused commit sends.
        TimeSpan pass = AddressHop.TotalRequestTimeout + 2 * CarrierHop.TotalRequestTimeout;

        (pass * FulfilmentWorker.ClaimBatchSize).ShouldBeLessThan(
            TimeSpan.FromSeconds(FulfilmentWorker.LeaseSeconds),
            "a lease that lapsed mid-pass would let a second replica claim a row this one is still booking");

        // §15.3's thirty-second drain is met by the stopping token, so the budget has to fit only the longest call.
        CarrierHop.TotalRequestTimeout.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void The_meter_factory_hands_every_creator_of_the_meter_name_one_meter()
    {
        IMeterFactory factory = _factory.Services.GetRequiredService<IMeterFactory>();

        // One meter per name.
        factory.Create(CarrierMetrics.MeterName).ShouldBeSameAs(factory.Create(CarrierMetrics.MeterName));
    }

    /// <summary>A known address with one field a character past its column, in a script no other field uses.</summary>
    private static StubAddress Widened(string field)
    {
        StubAddress valid = new(Guid.CreateVersion7(), "1 Abay Avenue", null, "Almaty", "050000", "KZ");

        return field switch
        {
            "line1" => valid with { Line1 = new string('Ж', AddressLimits.MaxLineLength + 1) },
            "line2" => valid with { Line2 = new string('Ж', AddressLimits.MaxLineLength + 1) },
            "city" => valid with { City = new string('Ж', AddressLimits.MaxCityLength + 1) },
            "post_code" => valid with { PostalCode = new string('Ж', AddressLimits.MaxPostalCodeLength + 1) },
            "country" => valid with { Country = "ЖЖЖ" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "not a field of the reply")
        };
    }

    /// <summary>The host with <c>Program</c>'s own token source left in place.</summary>
    private sealed class ProgramTokensFactory() : ShippingWorkerFactory(Unreachable.Sql, Unreachable.Rabbit)
    {
        protected override void ConfigureTokens(IServiceCollection services)
        {
        }
    }

    /// <summary>The identity provider's refusal as <c>CachingTokenClient</c> throws it (§11.5).</summary>
    private sealed class RefusingTokenCache : ITokenCache
    {
        public Task<string> GetAsync(string scope, CancellationToken ct) =>
            throw new InvalidOperationException("The token endpoint refused this host's client credentials.");
    }
}
