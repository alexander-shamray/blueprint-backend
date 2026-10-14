using Common.Application;

namespace Notifications.Application.Devices.RegisterDevice;

/// <summary>An app's start or token rotation, for the subject the caller's own token names (ADR-117).</summary>
/// <remarks>Not an idempotent command: the sequence is what orders a repeat, so no key is claimed (ADR-058).</remarks>
public sealed record RegisterDeviceCommand(
    Guid SubjectId,
    Guid InstallationId,
    string Platform,
    string AppId,
    string Token,
    long Sequence) : ICommand<Result<DeviceWrite>>;
