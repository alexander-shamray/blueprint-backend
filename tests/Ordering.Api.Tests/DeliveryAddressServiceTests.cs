using Grpc.Core;
using Grpc.Net.Client;
using Ordering.Api;
using Ordering.Delivery.V1;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// ADR-052's method over the real pipeline: authentication, the permission
/// policy, the dispatcher and Dapper on a real database.
/// </summary>
/// <remarks>
/// Over <c>TestServer</c>: <c>CreateHandler()</c> bypasses the network, so
/// the h2c negotiation a real Kestrel would need never happens here.
/// Whether the endpoint is declared <c>Http2</c> is the server's decision
/// and belongs against a real Kestrel (§9.7).
/// </remarks>
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

    /// <summary>
    /// The principal a validated <c>shipping-worker</c> token becomes (§11.3),
    /// as call metadata.
    /// </summary>
    /// <remarks>
    /// Passed per call rather than baked into the channel: a default grant is
    /// how a suite ends up proving a policy is applied while never once
    /// arriving without it.
    /// </remarks>
    private static Metadata Worker() =>
    [
        new Metadata.Entry(TestAuthHandler.UserHeader, "service-account-shipping-worker"),
        new Metadata.Entry(TestAuthHandler.PermissionsHeader, OrderingPermissions.DeliveryAddress)
    ];

    /// <summary>
    /// A person holding every permission this service's vocabulary can grant a
    /// person, including the admin claim that overrides the ownership check.
    /// </summary>
    /// <remarks>
    /// <c>orders:admin</c> is spelt as a literal because §11.4 keeps it out of
    /// <c>OrderingPermissions</c>: it is a claim <c>CancelOrderHandler</c>
    /// reads and not a policy an endpoint names.
    /// </remarks>
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
        // No principal: the channel with no metadata. Without this the whole
        // credential mechanism ADR-052 mints could be missing and every other
        // test here would still be green.
        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(Guid.CreateVersion7()), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task A_person_holding_every_user_permission_is_PermissionDenied()
    {
        // The half the first test cannot make: a host that stopped routing
        // answers Unauthenticated to everything, so "no token is refused" is
        // satisfiable by a service that is not there. This one says the
        // permission is doing the work — orders:delivery-address belongs to a
        // host and to no person (ADR-052), so even the admin claim is refused.
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

        // Nothing of the order travels. A reply that grew a status or a total
        // would make the reader able to decide things ADR-052 keeps here.
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
        // ADR-052's "does not exist is wider than a missing record": all three
        // cases answer NotFound so the client maps a status and never reads an
        // order's state.
        Guid order = await fixture.SeedOrderAsync(Guid.CreateVersion7());
        await fixture.ExecuteAsync(
            "UPDATE ordering.Orders SET Status = 'Cancelled' WHERE Id = {0};", order);

        StatusCode status = await StatusOfAsync(
            () => Addresses
                .GetAsync(For(order), Worker(), cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        status.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task An_order_whose_address_erasure_has_cleared_is_NotFound()
    {
        // §11.7's extension is owed and this is what it will produce: the
        // order's own record whole and its address gone. Written as raw SQL
        // because no consumer produces it yet, which is exactly ADR-052's
        // instruction to design against it rather than meet it later.
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

        // Untranslated this is Unknown, which rides grpc-status on an HTTP 200
        // and would reach the worker as neither an answer nor a transient
        // fault — so the shipment would back off for ever on a request that
        // can never succeed.
        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);

        // The field, never the value: it is a caller-supplied string arriving
        // in a message that reaches the logs, and §13.4's redactor cannot see a
        // value interpolated into one.
        thrown.Status.Detail.ShouldContain("order_id");
        thrown.Status.Detail.ShouldNotContain("not-a-guid");
    }
}
