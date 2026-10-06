using System.Net;
using System.Net.Http.Json;
using Payments.Application.Admin.GetPayment;
using Payments.Domain.Intents;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>§6.5's read side through its endpoint, over the migrated schema its SQL names.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PaymentEndpointsTests(ServiceFixture fixture) : IAsyncLifetime
{
    // Four distinct instants, an hour and a day apart, so a crossed pair cannot pass.
    private static readonly DateTimeOffset Placed = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cancelled = new(2026, 3, 2, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset IntentCreated = new(2026, 3, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Voided = new(2026, 3, 4, 13, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_refunded_payment_reads_its_record_intent_and_refund()
    {
        Guid order = Guid.CreateVersion7();
        await SeedAsync(order, authorised: true, refunded: true);

        using HttpClient client = Admin();

        PaymentView? view = await client.GetFromJsonAsync<PaymentView>(
            $"/v1/payments/{order}",
            TestContext.Current.CancellationToken);

        view.ShouldNotBeNull();
        view.OrderId.ShouldBe(order);
        view.Intent!.Status.ShouldBe("Authorised");
        view.Intent.Reference.ShouldBe("psp_x");
        view.Intent.DeclineReason.ShouldBeNull();
        view.Refund!.Reference.ShouldBe("psp_x");

        // The money, and the currency the char(3) column round-trips.
        view.Intent.Amount.ShouldBe(42.10m);
        view.Intent.Currency.ShouldBe("EUR");

        // Each stamp against the instant that seeded it, since a crossed pair leaves every one of them set.
        view.Order.PlacedAt.ShouldBe(Placed);
        view.Order.CancelledAt.ShouldBe(Cancelled);
        view.Intent.CreatedAt.ShouldBe(IntentCreated);
        view.Refund.VoidedAt.ShouldBe(Voided);
    }

    [Fact]
    public async Task A_placed_order_with_no_command_yet_reads_no_intent()
    {
        // The outer joins, load-bearing because the record exists before any command runs.
        Guid order = Guid.CreateVersion7();
        await SeedAsync(order, authorised: false, refunded: false);

        using HttpClient client = Admin();

        PaymentView? view = await client.GetFromJsonAsync<PaymentView>(
            $"/v1/payments/{order}",
            TestContext.Current.CancellationToken);

        view!.Intent.ShouldBeNull();
        view.Refund.ShouldBeNull();
    }

    [Fact]
    public async Task A_declined_intent_reads_its_reason_and_no_reference()
    {
        // A decline carries a reason and no reference, so a positional swap passes the case above and fails here.
        Guid order = Guid.CreateVersion7();
        await SeedDeclinedAsync(order);

        using HttpClient client = Admin();

        PaymentView? view = await client.GetFromJsonAsync<PaymentView>(
            $"/v1/payments/{order}",
            TestContext.Current.CancellationToken);

        view!.Intent!.Status.ShouldBe("Declined");
        view.Intent.DeclineReason.ShouldBe(DeclineReasons.OrderCancelled);
        view.Intent.Reference.ShouldBeNull();
        view.Refund.ShouldBeNull();
    }

    [Fact]
    public async Task The_answer_names_no_payer()
    {
        // Asserted over the serialised body, since widening the SELECT is all a leak would take.
        Guid order = Guid.CreateVersion7();
        await SeedAsync(order, authorised: true, refunded: false);

        using HttpClient client = Admin();

        string body = await client.GetStringAsync($"/v1/payments/{order}", TestContext.Current.CancellationToken);

        body.ShouldNotContain("customer", Case.Insensitive);
        body.ShouldNotContain("payer", Case.Insensitive);
    }

    [Fact]
    public async Task An_order_Payments_never_heard_of_reads_404()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/payments/{Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_the_permission_it_answers_403()
    {
        // Authenticated and unauthorised, the pair §11.3 asks every service to re-validate for itself.
        using HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/payments/{Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private HttpClient Admin()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, PaymentsPermissions.Admin);

        return client;
    }

    /// <summary>Seeds the tables directly, since no command reaches a refund without a provider round trip.</summary>
    private Task SeedAsync(Guid order, bool authorised, bool refunded) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt, CancelledAt)
            VALUES ({0}, NEWID(), 42.10, 'EUR', {3}, CASE WHEN {2} = 1 THEN {4} END);
            IF {1} = 1
                INSERT INTO payments.PaymentIntents (OrderId, Status, Amount, Currency, Reference, CreatedAt)
                VALUES ({0}, 'Authorised', 42.10, 'EUR', 'psp_x', {5});
            IF {2} = 1
                INSERT INTO payments.Refunds (OrderId, Reference, Amount, Currency, VoidedAt)
                VALUES ({0}, 'psp_x', 42.10, 'EUR', {6});
            """,
            order,
            authorised ? 1 : 0,
            refunded ? 1 : 0,
            Placed,
            Cancelled,
            IntentCreated,
            Voided);

    /// <summary>A placed order whose intent was declined with ADR-049's own reason, one this service mints.</summary>
    private Task SeedDeclinedAsync(Guid order) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt)
            VALUES ({0}, NEWID(), 42.10, 'EUR', {2});
            INSERT INTO payments.PaymentIntents (OrderId, Status, Amount, Currency, DeclineReason, CreatedAt)
            VALUES ({0}, 'Declined', 42.10, 'EUR', {1}, {3});
            """,
            order,
            DeclineReasons.OrderCancelled,
            Placed,
            IntentCreated);
}
