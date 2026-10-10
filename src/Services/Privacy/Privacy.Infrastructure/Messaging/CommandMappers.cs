using Common.Application;
using Common.Contracts.Privacy.V1;
using Privacy.Application.ErasureRequests.RecordCompletion;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Infrastructure.Messaging;

/// <summary>One mapper per command in §3.2's Accepts column: the wire-to-command boundary (§9.4).</summary>
/// <remarks>A failed parse throws <see cref="ContractMappingException"/>, which the endpoint does not retry (§9.8).</remarks>
public sealed class PersonalDataDeleteCompletedMapper
    : ICommandMessageMapper<PersonalDataDeleteCompleted, RecordErasureCompletionCommand>
{
    public RecordErasureCompletionCommand Map(PersonalDataDeleteCompleted message)
    {
        // A holder that names itself in a shape no request could have been raised with is a deployment fault, and
        // no amount of backoff resolves it. A valid name outside the set is the aggregate's to flag (ADR-092).
        if (!ErasureRequest.IsResponderName(message.Responder))
        {
            throw new ContractMappingException(
                $"Unusable responder on {nameof(PersonalDataDeleteCompleted)}.");
        }

        return new RecordErasureCompletionCommand(message.RequestId, message.Responder, message.Count);
    }
}
