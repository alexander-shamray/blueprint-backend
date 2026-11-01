using Common.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>ADR-191's rule apart from the broker, at the names the reference domain's endpoints use.</summary>
public sealed class SourceExchangeTests
{
    private const string Saga = "ordering-fulfilment-saga";
    private const string PaymentAuthorised = "Common.Contracts.Payments.V1:PaymentAuthorised";

    [Theory]
    [InlineData(PaymentAuthorised, "the type's own exchange")]
    [InlineData(Saga, "the endpoint's own exchange, which a send and a replay publish to")]
    [InlineData(Saga + "_delay", "the delayed-message exchange in front of it, which a timeout and a redelivery take")]
    public void Admits_the_types_exchange_and_the_endpoints(string exchange, string why) =>
        SourceExchange.Admits(exchange, PaymentAuthorised, Saga).ShouldBeTrue(why);

    [Theory]
    [InlineData("Common.Contracts.Inventory.V1:StockReleased", "#1684: a peer's own exchange, bound to this queue")]
    [InlineData("", "the default exchange, which routes by queue name to anyone with write on it")]
    [InlineData("common.contracts.payments.v1:paymentauthorised", "a broker name is compared as it is spelled")]
    [InlineData("ordering-fulfilment-saga-x", "a longer name that starts with the endpoint's")]
    [InlineData(PaymentAuthorised + "_delay", "the type's exchange takes no delay suffix")]
    public void Refuses_any_other(string exchange, string why) =>
        SourceExchange.Admits(exchange, PaymentAuthorised, Saga).ShouldBeFalse(why);
}
