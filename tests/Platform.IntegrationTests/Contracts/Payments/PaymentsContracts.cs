using Common.Contracts.Payments.V1;
using static Platform.IntegrationTests.Contracts.SampleValues;

namespace Platform.IntegrationTests.Contracts.Payments;

/// <summary>Payments' samples, and the one money-movement command it accepts (ADR-028).</summary>
internal sealed class PaymentsContracts : ContractContext
{
    public override string Service => "Payments";

    public override IReadOnlyDictionary<Type, Func<object>> Samples { get; } = new Dictionary<Type, Func<object>>
    {
        [typeof(PaymentAuthorised)] = () => new PaymentAuthorised
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            Reference = "psp_ref_9f21",
            Amount = 999.98m,
            Currency = "EUR"
        },
        [typeof(PaymentDeclined)] = () => new PaymentDeclined
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            Reason = "insufficient_funds"
        },
        [typeof(PaymentRefunded)] = () => new PaymentRefunded
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            Reference = "psp_ref_9f21",
            Amount = 999.98m,
            Currency = "EUR"
        },
        [typeof(AuthorisePayment)] = () => new AuthorisePayment(Order, 999.98m, "EUR")
    };

    public override Type[] CommandRoots => [typeof(AuthorisePayment)];

    public override (Type Contract, string Member)[] ApprovedCommandMembers =>
    [
        (typeof(AuthorisePayment), "OrderId"),
        (typeof(AuthorisePayment), "Amount"),      // instruction, not authority
        (typeof(AuthorisePayment), "Currency")     // the same
    ];
}
