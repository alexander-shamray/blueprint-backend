namespace Web.Bff.Devices;

/// <summary>ADR-117's bounds on a registration, as this host checks them before the hop.</summary>
/// <remarks>
/// Notifications' <c>DeviceLimits</c> owns each value and checks it again, since the BFF is not all that can reach
/// the method; these are its contract as devices.proto's comments state it, not a second decision (ADR-117).
/// </remarks>
public static class DeviceRules
{
    /// <summary>The two providers a token belongs to.</summary>
    public static readonly IReadOnlyList<string> Platforms = ["fcm", "apns"];

    public const int MaxAppIdLength = 255;

    public const int MaxTokenLength = 1024;

    /// <summary>Visible ASCII alone, so no bounded value can carry a space or a control character.</summary>
    public static bool IsPrintableAscii(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(c => c is >= '!' and <= '~');

    public static bool IsAppId(string? value) => IsPrintableAscii(value) && value!.Length <= MaxAppIdLength;
}
