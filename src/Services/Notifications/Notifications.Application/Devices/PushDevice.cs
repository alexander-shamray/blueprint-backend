namespace Notifications.Application.Devices;

/// <summary>A device row as a push send reads it; every field but the id is null on a cleared row (ADR-117).</summary>
/// <remarks>Its text names the installation alone, so no log line that prints one holds the token (ADR-117).</remarks>
public sealed record PushDevice(
    Guid InstallationId,
    Guid? SubjectId,
    string? Platform,
    string? AppId,
    string? Token)
{
    public override string ToString() => $"{nameof(PushDevice)} {{ {nameof(InstallationId)} = {InstallationId} }}";
}
