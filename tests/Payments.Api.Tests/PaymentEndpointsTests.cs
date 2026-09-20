using System.Net;
using System.Net.Http.Json;
using Payments.Application.Admin.GetPayment;
using Payments.Domain.Intents;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §6.5's read side over the migrated schema, which is where this query's SQL
/// is first checked against the tables it names: there is no in-memory stand-in
/// for Dapper worth writing, so the handler is tested through its endpoint.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PaymentEndpointsTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_refunded_payment_reads_its_record_intent_and_refund()
    {
        Guid order = Guid.CreateVersion7();
        await SeedAsync(order, authorised: true, refunded: true);

        using HttpClient client = Admin();

        PaymentView? view = await client.GetFromJsonAsync<PaymentView>(
            $"/v1/payments/{order}", TestContext.Current.CancellationToken);

        view.ShouldNotBeNull();
        view.OrderId.ShouldBe(order);
        view.Intent!.Status.ShouldBe("Authorised");
        view.Intent.Reference.ShouldBe("psp_x");
        view.Intent.DeclineReason.ShouldBeNull();
        view.Refund!.Reference.ShouldBe("psp_x");

        // The money, and the currency the char(3) column round-trips.
        view.Intent.Amount.ShouldBe(42.10m);
        view.Intent.Currency.ShouldBe("EUR");

        // Every timestamp, because four of them are read out of one joined row
        // and three share a type: a pair crossed between the SELECT and the
        // Row record leaves each one populated and each one wrong, which an
        // assertion on a single stamp cannot see.
        view.Order.PlacedAt.ShouldNotBeNull();
        view.Order.CancelledAt.ShouldNotBeNull();
        view.Intent.CreatedAt.ShouldNotBe(default);
        view.Refund.VoidedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task A_placed_order_with_no_command_yet_reads_no_intent()
    {
        // The two outer joins, asserted where they are load-bearing: the record
        // arrives on OrderPlaced and nothing else exists until a command runs,
        // so an inner join here would answer 404 for an order Payments holds.
        Guid order = Guid.CreateVersion7();
        await SeedAsync(order, authorised: false, refunded: false);

        using HttpClient client = Admin();

        PaymentView? view = await client.GetFromJsonAsync<PaymentView>(
            $"/v1/payments/{order}", TestContext.Current.CancellationToken);

        view!.Intent.ShouldBeNull();
        view.Refund.ShouldBeNull();
    }

    [Fact]
    public async Task A_declined_intent_reads_its_reason_and_no_reference()
    {
        // The other half of the intent projection: a decline carries a reason
        // and no provider reference, where an authorisation carries the
        // reverse. Both are nullable columns read positionally out of one
        // joined row, so a pair swapped in the SELECT or the record would
        // satisfy the authorised case above and be wrong here.
        Guid order = Guid.CreateVersion7();
        await SeedDeclinedAsync(order);

        using HttpClient client = Admin();

        PaymentView? view = await client.GetFromJsonAsync<PaymentView>(
            $"/v1/payments/{order}", TestContext.Current.CancellationToken);

        view!.Intent!.Status.ShouldBe("Declined");
        view.Intent.DeclineReason.ShouldBe(DeclineReasons.OrderCancelled);
        view.Intent.Reference.ShouldBeNull();
        view.Refund.ShouldBeNull();
    }

    [Fact]
    public async Task The_answer_names_no_payer()
    {
        // Asserted over the serialised body rather than the record's shape: the
        // column is on the table this query reads, so widening the SELECT is
        // all it would take (spec, section 10).
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
            $"/v1/payments/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_the_permission_it_answers_403()
    {
        // Authenticated and unauthorised, which is the pair §11.3 asks every
        // service to re-validate for itself: the gateway's route already
        // refused this caller, and that refusal is not this service's evidence.
        using HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/payments/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private HttpClient Admin()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, PaymentsPermissions.Admin);

        return client;
    }

    /// <summary>
    /// The three tables directly, because no command reaches the refund without
    /// a provider round trip the other suites own. Placeholders are
    /// <c>{0}</c>-style, which the fixture turns into real SQL parameters.
    /// </summary>
    private Task SeedAsync(Guid order, bool authorised, bool refunded) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt, CancelledAt)
            VALUES ({0}, NEWID(), 42.10, 'EUR', SYSDATETIMEOFFSET(), CASE WHEN {2} = 1 THEN SYSDATETIMEOFFSET() END);
            IF {1} = 1
                INSERT INTO payments.PaymentIntents (OrderId, Status, Amount, Currency, Reference, CreatedAt)
                VALUES ({0}, 'Authorised', 42.10, 'EUR', 'psp_x', SYSDATETIMEOFFSET());
            IF {2} = 1
                INSERT INTO payments.Refunds (OrderId, Reference, Amount, Currency, VoidedAt)
                VALUES ({0}, 'psp_x', 42.10, 'EUR', SYSDATETIMEOFFSET());
            """,
            order,
            authorised ? 1 : 0,
            refunded ? 1 : 0);

    /// <summary>
    /// A placed order whose intent was declined: no reference, a reason, and
    /// ADR-049's own reason rather than a provider code, because that is the
    /// one this service mints itself.
    /// </summary>
    private Task SeedDeclinedAsync(Guid order) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt)
            VALUES ({0}, NEWID(), 42.10, 'EUR', SYSDATETIMEOFFSET());
            INSERT INTO payments.PaymentIntents (OrderId, Status, Amount, Currency, DeclineReason, CreatedAt)
            VALUES ({0}, 'Declined', 42.10, 'EUR', {1}, SYSDATETIMEOFFSET());
            """,
            order,
            DeclineReasons.OrderCancelled);
}
