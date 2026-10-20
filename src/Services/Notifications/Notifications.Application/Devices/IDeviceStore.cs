using Notifications.Application.Devices.RegisterDevice;
using Notifications.Application.Devices.SignOutDevice;

namespace Notifications.Application.Devices;

/// <summary>ADR-117's device rows, one per installation, each write decided by the row's own sequence.</summary>
public interface IDeviceStore
{
    /// <summary>
    /// Upserts the installation's row for the caller, inside the unit of work's transaction, stamped with the erasure
    /// requests already applied to the caller (ADR-138).
    /// </summary>
    Task<DeviceWrite> RegisterAsync(
        RegisterDeviceCommand command,
        IReadOnlyList<Guid> erasedUnder,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>Clears the caller's row to its id and sequence, inside the unit of work's transaction.</summary>
    Task<DeviceWrite> SignOutAsync(SignOutDeviceCommand command, DateTimeOffset now, CancellationToken ct);
}
