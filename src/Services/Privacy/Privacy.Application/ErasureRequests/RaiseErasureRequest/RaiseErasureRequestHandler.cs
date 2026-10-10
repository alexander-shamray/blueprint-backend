using Common.Application;
using Microsoft.Extensions.Options;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.ErasureRequests.RaiseErasureRequest;

/// <summary>Opens a request for a subject, or answers with the one already open and publishes nothing (ADR-092).</summary>
public sealed class RaiseErasureRequestHandler(
    IErasureRequestRepository requests,
    IOptions<PrivacyOptions> options,
    TimeProvider clock)
    : ICommandHandler<RaiseErasureRequestCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(RaiseErasureRequestCommand command, CancellationToken ct)
    {
        ErasureRequest? open = await requests.GetUnclosedForSubjectAsync(command.SubjectId, ct);
        if (open is not null)
            return Result.Success(open.Id);

        PrivacyOptions settings = options.Value;
        ErasureRequest request = ErasureRequest.Raise(
            Guid.CreateVersion7(),
            command.SubjectId,
            settings.Responders,
            settings.CompletionSlo,
            clock.GetUtcNow());

        requests.Add(request);

        return Result.Success(request.Id);
    }
}
