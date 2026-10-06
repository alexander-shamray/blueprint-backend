using Grpc.Core;
using Grpc.Net.Client;
using Ordering.Api;
using Ordering.Delivery.V1;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>ADR-052's method over the real pipeline: authentication, policy, dispatcher and Dapper.</summary>
/// <remarks>Over <c>TestServer</c>, which negotiates no h2c; <c>Http2</c> is Kestrel's to declare (§9.7).</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class DeliveryAddressServiceTests(ServiceFixture fixture) : IAsyncLifetime
{
    private GrpcChannel _channel = null!;

    public async ValueTask InitializeAsync()
    {
        _channel = GrpcChannel.ForAddress(
            fixture.Factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = fixture.Factory.Server.CreateHandler() });

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();

        return ValueTask.CompletedTask;
    }

    private DeliveryAddresses.DeliveryAddressesClient Addresses => new(_channel);

    /// <summary>The principal a validated <c>shipping-worker</c> token becomes (§11.3), passed per call.</summary>
    private static Metadata Worker() =>
    [
        new Metadata.Entry(TestAuthHandler.UserHeader, "service-account-shipping-worker"),
        new Metadata.Entry(TestAuthHandler.PermissionsHeader, OrderingPermissions.DeliveryAddress)
    ];

    /// <summary>A person holding every permission a person can be granted, the admin claim included.</summary>
    /// <remarks><c>orders:admin</c> is a literal, since §11.4 keeps it out of <c>OrderingPermissions</c>.</remarks>
    private static Metadata EveryUserPermission() =>
    [
        new Metadata.Entry(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString()),
        new Metadata.Entry(
            TestAuthHandler.PermissionsHeader,
            $"{OrderingPermissions.Write} {OrderingPermissions.Cancel} orders:admin")
    ];

    private static GetDeliveryAddressRequest For(Guid orderId) => new() { OrderId = orderId.ToString() };

    private static async Task<StatusCode> StatusOfAsync(Func<Task<GetDeliveryAddressReply>> call)
    {
        RpcException thrown = await Should.ThrowAsync<RpcException>(call);

        return thrown.StatusCode;
    }

    [Fact]
    public async Task A_caller_with_no_token_is_Unauthenticated()
    {
        // No principal: the channel with no metadata, refused without ADR-052's credential.
        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(Guid.CreateVersion7()), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task A_person_holding_every_user_permission_is_PermissionDenied()
    {
        // The permission does the work: it belongs to a host and to no person (ADR-052), so even admin is refused.
        Guid order = await fixture.SeedOrderAsync(Guid.CreateVersion7());

        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(order), EveryUserPermission(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task The_client_reads_the_address_and_the_customer_and_nothing_else()
    {
        Guid customer = Guid.CreateVersion7();
        Guid order = await fixture.SeedOrderAsync(customer);

        GetDeliveryAddressReply reply = await Addresses.GetAsync(
            For(order),
            Worker(),
            cancellationToken: TestContext.Current.CancellationToken);

        // SeedOrderAsync's address, field for field.
        reply.CustomerId.ShouldBe(customer.ToString());
        reply.Line1.ShouldBe("1 Test Street");
        reply.Line2.ShouldBe("", "proto3 has no null string, so an absent second line is empty");
        reply.City.ShouldBe("Almaty");
        reply.PostCode.ShouldBe("050000");
        reply.Country.ShouldBe("KZ");

        // Nothing of the order travels, so the reader can decide nothing ADR-052 keeps here.
        reply.ToString().ShouldNotContain("AwaitingStock");
        reply.ToString().ShouldNotContain("19.99");
    }

    [Fact]
    public async Task An_order_that_does_not_exist_is_NotFound()
    {
        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(
                    For(Guid.CreateVersion7()),
                    Worker(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task A_cancelled_order_is_NotFound_rather_than_an_address()
    {
        // ADR-052: "does not exist" is wider than a missing record, so all three cases answer NotFound.
        Guid order = await fixture.SeedOrderAsync(Guid.CreateVersion7());
        await fixture.ExecuteAsync("UPDATE ordering.Orders SET Status = 'Cancelled' WHERE Id = {0};", order);

        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(order), Worker(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task An_order_whose_address_erasure_has_cleared_is_NotFound()
    {
        // What §11.7's erasure will produce, staged as raw SQL since nothing produces it yet (ADR-052).
        Guid order = await fixture.SeedOrderAsync(Guid.CreateVersion7());
        await fixture.ExecuteAsync(
            """
            UPDATE ordering.Orders
            SET ShipToLine1 = '', ShipToLine2 = NULL, ShipToCity = '', ShipToPostalCode = '', ShipToCountry = ''
            WHERE Id = {0};
            """,
            order);

        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(order), Worker(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task A_malformed_order_id_is_InvalidArgument()
    {
        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Addresses
                .GetAsync(
                    new GetDeliveryAddressRequest { OrderId = "not-a-guid" },
                    Worker(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        // Untranslated this is Unknown, which the worker would back off on for ever.
        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);

        // The field, never the value, since §13.4's redactor cannot see a value interpolated into a message.
        thrown.Status.Detail.ShouldContain("order_id");
        thrown.Status.Detail.ShouldNotContain("not-a-guid");
    }
}
