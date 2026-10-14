using Common.Application;

namespace Notifications.Application.Devices.SignOutDevice;

/// <summary>Hands the sign-out to the store, whose one batch decides it against the row it locks.</summary>
public sealed class SignOutDeviceHandler(IDeviceStore store, TimeProvider clock)
    : ICommandHandler<SignOutDeviceCommand, Result<DeviceWrite>>
{
    public async Task<Result<DeviceWrite>> HandleAsync(SignOutDeviceCommand command, CancellationToken ct) =>
        Result.Success(await store.SignOutAsync(command, clock.GetUtcNow(), ct));
}
