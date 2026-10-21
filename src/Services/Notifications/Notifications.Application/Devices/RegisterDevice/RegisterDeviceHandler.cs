using Common.Application;
using Notifications.Application.Privacy;

namespace Notifications.Application.Devices.RegisterDevice;

/// <summary>Hands the registration to the store, whose one batch decides it against the row it locks.</summary>
/// <remarks>
/// The erasures are read first, so the row names only requests that ran before it and a reissue of one keeps it
/// (ADR-138).
/// </remarks>
public sealed class RegisterDeviceHandler(
    IDeviceStore store,
    IPersonalDataErasureRepository erasures,
    TimeProvider clock)
    : ICommandHandler<RegisterDeviceCommand, Result<DeviceWrite>>
{
    public async Task<Result<DeviceWrite>> HandleAsync(RegisterDeviceCommand command, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        IReadOnlyList<Guid> erasedUnder = await erasures.RequestsErasingAsync(command.SubjectId, now, ct);

        return Result.Success(await store.RegisterAsync(command, erasedUnder, now, ct));
    }
}
