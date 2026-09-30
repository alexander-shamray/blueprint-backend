using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// Keeps the reference width Payments mints and the one this service records from parting, since §4.2 keeps
/// <c>PaymentReference.MaxLength</c> from naming <see cref="PaymentLimits.MaxReferenceLength"/>.
/// </summary>
public class PaymentReferenceWidthTests
{
    private static readonly Guid Order = Guid.Parse("5f2c1a90-8b47-4d13-9e6a-2c48d7b0f315");

    [Fact]
    public void The_longest_reference_the_contract_admits_is_one_this_service_records()
    {
        // Through the mapper, where a reference Payments minted meets this service's guard.
        ConfirmOrderMapper mapper = new();
        string reference = new('r', PaymentLimits.MaxReferenceLength);

        ConfirmOrderCommand mapped = mapper.Map(new ConfirmOrder(Order, reference));

        mapped.Reference.Value.ShouldBe(reference);
    }

    [Fact]
    public void A_reference_longer_than_the_contract_admits_is_a_malformed_message()
    {
        ConfirmOrderMapper mapper = new();
        string reference = new('r', PaymentLimits.MaxReferenceLength + 1);

        Should.Throw<ContractMappingException>(
            () => mapper.Map(new ConfirmOrder(Order, reference)));
    }
}
