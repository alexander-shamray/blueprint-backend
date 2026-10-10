using Common.Contracts.Privacy.V1;
using MassTransit;
using Web.Bff.Privacy;

namespace Web.Bff.Messaging;

/// <summary>Sends the completion from the consumer's scope, so the endpoint's in-memory outbox holds it.</summary>
/// <remarks>A send lost between commit and release is silence, which an overdue request answers (ADR-094).</remarks>
internal sealed class ErasureReporter(ISendEndpointProvider sends) : IErasureReporter
{
    // The name Privacy's responder set holds for this service (ADR-092).
    private const string Responder = "bff";

    private static readonly Uri PrivacyCompletionsQueue = new("queue:privacy-completions");

    public async Task ReportAsync(Guid requestId, int count, CancellationToken ct)
    {
        ISendEndpoint endpoint = await sends.GetSendEndpoint(PrivacyCompletionsQueue);

        await endpoint.Send(new PersonalDataDeleteCompleted(requestId, Responder, count), ct);
    }
}
