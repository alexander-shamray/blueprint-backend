using Common.Application;
using Common.Domain;

namespace Ordering.Application.Privacy.CloseErasure;

/// <summary>Records that a request Ordering answered has closed, so a check whose data is newer skips it
/// (ADR-139).</summary>
/// <remarks>A request with no row here is one Ordering has not answered, as one an amendment removed it from first
/// (ADR-136), so there is nothing to stamp.</remarks>
public sealed class CloseErasureHandler(IPersonalDataErasureRepository erasures)
    : ICommandHandler<CloseErasureCommand, Result>
{
    public async Task<Result> HandleAsync(CloseErasureCommand command, CancellationToken ct)
    {
        PersonalDataErasure? seen = await erasures.GetAsync(command.RequestId, ct);

        seen?.MarkClosed(command.ClosedAt);

        return Result.Success();
    }
}
