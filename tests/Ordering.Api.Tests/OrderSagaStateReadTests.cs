using System.Net;
using System.Net.Http.Json;
using Ordering.Application.Orders.GetSagaState;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>#1092's read of the saga's own state over HTTP, against the table the saga writes (§9.6).</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderSagaStateReadTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Staff = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_reader_gets_the_state_the_saga_row_holds()
    {
        Guid order = await SeedSagaAsync("AwaitingPayment", cancelReason: null);

        HttpResponseMessage response = await GetAsync(order, OrderingPermissions.SagaRead);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SagaStateView view = (await response.Content.ReadFromJsonAsync<SagaStateView>(
            TestContext.Current.CancellationToken))!;
        view.OrderId.ShouldBe(order);
        view.CurrentState.ShouldBe("AwaitingPayment");
        view.CancelReason.ShouldBeNull();
        view.StartedAt.ShouldBe(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_compensating_saga_reports_why()
    {
        Guid order = await SeedSagaAsync("Compensating", cancelReason: "payment_declined");

        HttpResponseMessage response = await GetAsync(order, OrderingPermissions.SagaRead);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SagaStateView view = (await response.Content.ReadFromJsonAsync<SagaStateView>(
            TestContext.Current.CancellationToken))!;
        view.CurrentState.ShouldBe("Compensating");
        view.CancelReason.ShouldBe("payment_declined");
    }

    [Fact]
    public async Task No_row_is_404_whether_the_saga_finished_or_the_order_never_existed()
    {
        // A finalised saga deletes its instance, so both facts read the same (§9.6).
        HttpResponseMessage response = await GetAsync(Guid.CreateVersion7(), OrderingPermissions.SagaRead);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("orders:write")]
    [InlineData("orders:cancel")]
    public async Task A_caller_without_the_read_permission_gets_403_for_a_row_that_exists(string permissions)
    {
        // Another order permission is no stand-in: the policy refuses before any row is read.
        Guid order = await SeedSagaAsync("Confirmed", cancelReason: null);

        HttpResponseMessage response = await GetAsync(order, permissions);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unauthenticated_caller_gets_401()
    {
        Guid order = await SeedSagaAsync("Confirmed", cancelReason: null);

        HttpResponseMessage response = await GetAsync(order, permissions: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<Guid> SeedSagaAsync(string state, string? cancelReason)
    {
        Guid order = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            """
            INSERT INTO ordering.OrderFulfilmentStates
                (CorrelationId, CurrentState, OrderId, Total, Currency, StartedAt, CancelReason)
            VALUES ({0}, {1}, {0}, 10.0000, 'EUR', '2026-10-16T08:00:00+00:00', NULLIF({2}, ''))
            """,
            order,
            state,
            cancelReason ?? string.Empty);

        return order;
    }

    private Task<HttpResponseMessage> GetAsync(Guid order, string? permissions)
    {
        HttpClient client = fixture.Factory.CreateClient();
        if (permissions is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Staff.ToString());
            client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, permissions);
        }

        return client.GetAsync($"/v1/orders/{order}/saga", TestContext.Current.CancellationToken);
    }
}
