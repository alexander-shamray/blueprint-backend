using Common.Contracts.Privacy.V1;
using static Platform.IntegrationTests.Contracts.SampleValues;

namespace Platform.IntegrationTests.Contracts.Privacy;

/// <summary>Privacy's samples, and the one command it receives back from the holders (ADR-094).</summary>
internal sealed class PrivacyContracts : ContractContext
{
    public override string Service => "Privacy";

    public override IReadOnlyDictionary<Type, Func<object>> Samples { get; } = new Dictionary<Type, Func<object>>
    {
        [typeof(PersonalDataDeleteRequested)] = () => new PersonalDataDeleteRequested
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            RequestId = Request,
            SubjectId = Customer
        },
        [typeof(PersonalDataDeleteClosed)] = () => new PersonalDataDeleteClosed
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            RequestId = Request
        },
        [typeof(PersonalDataDeleteCompleted)] = () => new PersonalDataDeleteCompleted(Request, "shipping", 3)
    };

    public override Type[] CommandRoots => [typeof(PersonalDataDeleteCompleted)];

    public override (Type Contract, string Member)[] ApprovedCommandMembers =>
    [
        (typeof(PersonalDataDeleteCompleted), "RequestId"),
        (typeof(PersonalDataDeleteCompleted), "Responder"),   // the holder's claim, checked by Privacy (ADR-094)
        (typeof(PersonalDataDeleteCompleted), "Count")
    ];
}
