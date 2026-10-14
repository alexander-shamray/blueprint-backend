using Common.Application;

namespace Notifications.Application.Devices.RegisterDevice;

/// <summary>Hands the registration to the store, whose one batch decides it against the row it locks.</summary>
public sealed class RegisterDeviceHandler(IDeviceStore store, TimeProvider clock)
    : ICommandHandler<RegisterDeviceCommand, Result<DeviceWrite>>
{
    public async Task<Result<DeviceWrite>> HandleAsync(RegisterDeviceCommand command, CancellationToken ct) =>
        Result.Success(await store.RegisterAsync(command, clock.GetUtcNow(), ct));
}
