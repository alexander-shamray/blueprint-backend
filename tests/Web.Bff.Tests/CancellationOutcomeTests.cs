using Common.Contracts.Ordering.V1;
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§10.7's table, a row per line, keyed on <c>Origin</c> before <c>Reason</c>.</summary>
public sealed class CancellationOutcomeTests
{
    [Theory]
    [InlineData(CancelOrigins.User, CancelReasons.OutOfStock, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.User, CancelReasons.StockTimeout, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.User, CancelReasons.PaymentDeclined, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.User, CancelReasons.PaymentTimeout, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.User, CancelReasons.CustomerRequest, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.OutOfStock, BuyerStatuses.OutOfStock)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.StockTimeout, BuyerStatuses.OutOfStock)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.PaymentDeclined, BuyerStatuses.Declined)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.PaymentTimeout, BuyerStatuses.Declined)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.CustomerRequest, BuyerStatuses.Cancelled)]
    [InlineData(null, CancelReasons.OutOfStock, BuyerStatuses.Cancelled)]
    [InlineData(null, CancelReasons.PaymentDeclined, BuyerStatuses.Cancelled)]
    [InlineData(null, CancelReasons.CustomerRequest, BuyerStatuses.Cancelled)]
    public void Each_line_of_the_table_maps_as_it_reads(string? origin, string reason, string expected) =>
        CancellationOutcome.Of(origin, reason).ShouldBe(expected);

    [Theory]
    [InlineData(CancelOrigins.User)]
    [InlineData(CancelOrigins.Workflow)]
    [InlineData(null)]
    [InlineData("operator")]
    public void A_reason_outside_the_vocabulary_claims_least_under_every_origin(string? origin) =>
        CancellationOutcome.Of(origin, "fraud_suspected").ShouldBe(
            BuyerStatuses.Cancelled,
            "a code a newer publisher adds is no evidence the platform refused anything (§10.7)");

    [Theory]
    [InlineData(CancelReasons.OutOfStock)]
    [InlineData(CancelReasons.PaymentDeclined)]
    public void An_origin_outside_the_vocabulary_claims_least_too(string reason) =>
        CancellationOutcome.Of("operator", reason).ShouldBe(BuyerStatuses.Cancelled);

    [Fact]
    public void A_buyer_who_typed_the_saga_s_reason_is_not_told_their_card_was_refused() =>
        CancellationOutcome.Of(CancelOrigins.User, CancelReasons.PaymentDeclined).ShouldBe(
            BuyerStatuses.Cancelled,
            "the cancel endpoint accepts all five codes, so Reason alone answers the wrong question (§10.7)");
}
