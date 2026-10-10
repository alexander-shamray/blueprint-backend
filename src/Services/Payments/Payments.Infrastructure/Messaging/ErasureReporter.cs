using Common.Contracts.Privacy.V1;
using MassTransit;
using Payments.Application.Privacy;

namespace Payments.Infrastructure.Messaging;

/// <summary>Sends the completion from the consumer's scope, so the endpoint's in-memory outbox holds it.</summary>
/// <remarks>A send lost between commit and release is silence, which an overdue request answers (ADR-094).</remarks>
internal sealed class ErasureReporter(ISendEndpointProvider sends, TimeProvider clock) : IErasureReporter
{
    // The name Privacy's responder set holds for this service (ADR-092).
    private const string Responder = "payments";

    public async Task ReportAsync(Guid requestId, int count, CancellationToken ct)
    {
        ISendEndpoint endpoint = await sends.GetSendEndpoint(Endpoints.PrivacyCompletionsQueue);

        await endpoint.Send(
            new PersonalDataDeleteCompleted
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = requestId,
                OccurredAt = clock.GetUtcNow(),
                RequestId = requestId,
                Responder = Responder,
                Count = count
            },
            ct);
    }
}
