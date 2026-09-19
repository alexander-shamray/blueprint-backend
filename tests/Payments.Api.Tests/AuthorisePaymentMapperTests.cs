using Common.Application;
using Common.Contracts.Payments.V1;
using Payments.Application.Intents.AuthorisePayment;
using Payments.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §9.4's wire-to-command boundary for §3.2's one accepted command,
/// constructed directly: every refusal below is the mapper's own, decided
/// before a dispatcher or a database is reached, so nothing here needs a
/// container.
/// </summary>
public sealed class AuthorisePaymentMapperTests
{
    [Theory]
    [InlineData(-1, "EUR")]
    [InlineData(1.001, "EUR")]
    [InlineData(1e15, "EUR")]
    [InlineData(1, "")]
    [InlineData(1, "EU")]
    [InlineData(1, "eur")]
    public void A_contract_no_order_could_have_produced_is_refused_before_the_handler(decimal amount, string currency)
    {
        Should.Throw<ContractMappingException>(() =>
            new AuthorisePaymentMapper().Map(new AuthorisePayment(Guid.CreateVersion7(), amount, currency)));
    }

    [Fact]
    public void A_zero_total_is_a_contract_an_order_can_produce()
    {
        Guid order = Guid.CreateVersion7();

        new AuthorisePaymentMapper().Map(new AuthorisePayment(order, 0m, "EUR"))
            .ShouldBe(new AuthorisePaymentCommand(order, 0m, "EUR"),
                "Money.Zero is a valid total in Catalog and Ordering, and the saga forwards it");
    }

    [Fact]
    public void An_empty_order_id_is_refused_rather_than_waited_for()
    {
        Should.Throw<ContractMappingException>(() =>
            new AuthorisePaymentMapper().Map(new AuthorisePayment(Guid.Empty, 42.10m, "EUR")));
    }

    [Fact]
    public void A_well_formed_contract_maps_field_for_field()
    {
        Guid order = Guid.CreateVersion7();

        new AuthorisePaymentMapper().Map(new AuthorisePayment(order, 42.10m, "EUR"))
            .ShouldBe(new AuthorisePaymentCommand(order, 42.10m, "EUR"));
    }
}
