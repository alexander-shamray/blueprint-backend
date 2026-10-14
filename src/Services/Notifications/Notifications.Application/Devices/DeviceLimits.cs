namespace Notifications.Application.Devices;

/// <summary>What ADR-117 bounds a device row by, named once for the validators and the table's widths.</summary>
public static class DeviceLimits
{
    /// <summary>The two providers a token belongs to; the platform decides which one a push goes through.</summary>
    public static readonly IReadOnlyList<string> Platforms = ["fcm", "apns"];

    /// <summary>The widest platform name, the table's column width.</summary>
    public const int MaxPlatformLength = 8;

    /// <summary>A bundle id or package name, short enough to keep the unique key under its ceiling.</summary>
    public const int MaxAppIdLength = 255;

    /// <summary>Well above either provider's token, and short enough for the unique key beside the app id.</summary>
    public const int MaxTokenLength = 1024;
}
