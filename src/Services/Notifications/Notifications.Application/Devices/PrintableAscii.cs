namespace Notifications.Application.Devices;

/// <summary>Visible ASCII alone, so no value ADR-117 bounds can carry a space or a control character.</summary>
public static class PrintableAscii
{
    public static bool Holds(string? value) => value is not null && value.All(c => c is >= '!' and <= '~');
}
