using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// The agreement between the reference width Payments may mint and the one
/// this service records. §4.2 holds <c>Ordering.Domain</c> to
/// <c>Common.Domain</c>, so <c>PaymentReference.MaxLength</c> cannot name
/// <see cref="PaymentLimits.MaxReferenceLength"/> and the width is two
/// literals; this suite keeps them from parting. A reference Payments accepts
/// and this service refuses is money authorised against an order that can
/// never be confirmed, because the mapper sends the message to the error
/// queue rather than the payment back.
/// </summary>
public class PaymentReferenceWidthTests
{
    private static readonly Guid Order = Guid.Parse("5f2c1a90-8b47-4d13-9e6a-2c48d7b0f315");

    [Fact]
    public void The_longest_reference_the_contract_admits_is_one_this_service_records()
    {
        // Driven through the mapper rather than compared as two numbers,
        // because the mapper is where a reference Payments minted meets this
        // service's guard and is therefore the only place a disagreement
        // between the two widths can be observed at all.
        ConfirmOrderMapper mapper = new();
        string reference = new('r', PaymentLimits.MaxReferenceLength);

        ConfirmOrderCommand mapped = mapper.Map(new ConfirmOrder(Order, reference));

        mapped.Reference.Value.ShouldBe(reference);
    }

    [Fact]
    public void A_reference_longer_than_the_contract_admits_is_a_malformed_message()
    {
        // The other side, and the one that keeps this service's guard from
        // being quietly widened past what Payments will ever send: a width
        // above the published one is a contract nobody minted against.
        ConfirmOrderMapper mapper = new();
        string reference = new('r', PaymentLimits.MaxReferenceLength + 1);

        Should.Throw<ContractMappingException>(
            () => mapper.Map(new ConfirmOrder(Order, reference)));
    }
}
