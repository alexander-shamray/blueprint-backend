using System.Text.Json;
using Common.Contracts.Ordering.V1;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests.Contracts.Ordering;

/// <summary>The checks that are about an Ordering contract and no other (§12.6, ADR-028).</summary>
public class OrderingContractTests
{
    [Fact]
    public void A_payload_predating_an_additive_member_still_deserialises()
    {
        // By hand, as a round-trip through today's contract cannot model a producer that never knew the member.
        string beforeTheField = """
            {"MessageId":"0199a1e0-0000-7000-8000-000000000001",
             "CorrelationId":"0199a1e0-0000-7000-8000-000000000002",
             "OccurredAt":"2026-08-25T12:00:00+00:00",
             "OrderId":"0199a1e0-0000-7000-8000-000000000003",
             "CustomerId":"0199a1e0-0000-7000-8000-000000000004",
             "Reason":"customer_request"}
            """;

        OrderCancelled? deserialised = JsonSerializer.Deserialize<OrderCancelled>(beforeTheField);

        deserialised.ShouldNotBeNull();
        deserialised.Origin.ShouldBeNull("absent is what §9.6's discard branch reads");
        deserialised.Reason.ShouldBe(CancelReasons.CustomerRequest);
    }

    [Fact]
    public void A_subject_is_detectable_on_a_contract_that_carries_one()
    {
        // The positive control, on the CustomerId ADR-028 requires OrderPlaced to keep.
        ContractTests.SubjectMembers(typeof(OrderPlaced))
            .Select(p => p.Name)
            .ShouldContain(nameof(OrderPlaced.CustomerId));
    }
}
