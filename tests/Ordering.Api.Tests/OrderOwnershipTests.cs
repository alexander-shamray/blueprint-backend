using System.Net;
using System.Net.Http.Json;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Outbox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Api.Endpoints;
using Ordering.Application.Orders;
using Ordering.Domain.Orders;
using Ordering.Infrastructure.Persistence;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§11.4's ownership check over HTTP against a real database.</summary>
/// <remarks>
/// Over the wire, because <c>ICurrentUser</c> is <c>HttpContextCurrentUser</c> and only a real request exercises
/// what answers "who is the caller" (§12.4).
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderOwnershipTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task User_A_cancelling_user_B_s_order_gets_404_and_not_403()
    {
        // 404 rather than 403, which would confirm the order exists; and the order must still be there after.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: Alice);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await StatusOfAsync(order)).ShouldBe(
            nameof(OrderStatus.AwaitingStock),
            "the 404 must be a refusal, not a cancellation reported as a miss");
    }

    [Fact]
    public async Task The_owner_can_cancel_their_own_order()
    {
        // The control, or the test above would pass on a handler that answers 404 to everybody.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: Bob);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.Cancelled));
    }

    [Fact]
    public async Task An_order_that_does_not_exist_is_the_same_404()
    {
        // Not-found and not-yours must look alike, or the pair becomes the oracle the 404 avoids.
        HttpResponseMessage response = await CancelAsync(new OrderId(Guid.CreateVersion7()), asUser: Alice);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_admin_claim_reaches_an_order_it_does_not_own()
    {
        // §11.4's one sanctioned override, a claim rather than a policy, since the order is not loaded yet then.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(
            order,
            asUser: Alice,
            permissions: $"{OrderingPermissions.Cancel} orders:admin");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.Cancelled));
    }

    [Fact]
    public async Task An_unauthenticated_caller_gets_401_and_never_reaches_the_handler()
    {
        // The group's RequireAuthorization, one of two independent refusals beside the handler's guard.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.AwaitingStock));
    }

    [Fact]
    public async Task A_caller_without_the_cancel_permission_gets_403()
    {
        // 403 is safe here: the policy refuses before any order is loaded, so it is the same for every id.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: Bob, permissions: "orders:write");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.AwaitingStock));
    }

    [Fact]
    public async Task A_cancellation_through_this_endpoint_publishes_the_user_origin()
    {
        // Only a real request supplies the principal a User-origin cancellation needs; tagged as the workflow's
        // echo, §9.6 would discard it on a missing instance rather than fault.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: Bob);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();

        row.Payload.ShouldContain(
            $"\"Origin\":\"{CancelOrigins.User}\"",
            Case.Sensitive,
            "a cancellation with a principal behind it is not this workflow's echo");
    }

    private async Task<OrderId> SeedOrderAsync(Guid customer) =>
        new(await fixture.SeedOrderAsync(customer));

    private Task<HttpResponseMessage> CancelAsync(
        OrderId order,
        Guid? asUser,
        string? permissions = null)
    {
        HttpClient client = fixture.Factory.CreateClient();
        if (asUser is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, asUser.ToString());
            client.DefaultRequestHeaders.Add(
                TestAuthHandler.PermissionsHeader,
                permissions ?? OrderingPermissions.Cancel);
        }

        return client.PostAsJsonAsync(
            $"/v1/orders/{order.Value}/cancel",
            new CancelOrderRequest(CancelReasons.CustomerRequest),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_reason_outside_the_wire_vocabulary_is_rejected()
    {
        // The enum's member name, which Enum.TryParse would accept and CancellationReasons refuses.
        OrderId order = await SeedOrderAsync(Bob);
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Bob.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Cancel);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/orders/{order.Value}/cancel",
            new CancelOrderRequest("CustomerRequest"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.AwaitingStock));
    }

    [Theory]
    [InlineData(nameof(OrderStatus.Shipped))]
    [InlineData(nameof(OrderStatus.Delivered))]
    public async Task An_order_past_despatch_is_refused_with_422_and_the_shipped_code(string status)
    {
        // §10.5's 422 and its code over HTTP. The status is arranged directly, since no transition sets Delivered.
        OrderId order = await SeedOrderAsync(Bob);
        await fixture.ExecuteAsync(
            "UPDATE ordering.Orders SET Status = {0} WHERE Id = {1}",
            status,
            order.Value);

        HttpResponseMessage response = await CancelAsync(order, Bob);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);

        problem.ShouldNotBeNull();
        problem.Extensions["code"]?.ToString().ShouldBe(
            "order.already_shipped",
            "the code is a §9.8 dimension value and splitting it would halve the series");
        // The exact string, since this customer-facing sentence must be true of both statuses.
        problem.Detail.ShouldBe(
            "An order that has already shipped cannot be cancelled; raise a return instead.",
            $"a {status} order's customer reads this, and naming one of the two " +
                "statuses is what #109 was filed for");

        (await StatusOfAsync(order)).ShouldBe(status, "a refusal must not have cancelled anything");
    }

    private Task<string> StatusOfAsync(OrderId order) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.Orders WHERE Id = {0}",
            order.Value);
}
