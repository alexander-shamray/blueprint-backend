using Common.Application;

namespace Notifications.Application.Devices.SignOutDevice;

/// <summary>An app's sign-out, naming its row by installation so no token travels in a URL (ADR-117).</summary>
public sealed record SignOutDeviceCommand(Guid SubjectId, Guid InstallationId, long Sequence)
    : ICommand<Result<DeviceWrite>>;
