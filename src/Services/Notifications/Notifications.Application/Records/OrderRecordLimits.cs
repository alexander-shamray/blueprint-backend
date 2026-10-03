namespace Notifications.Application.Records;

/// <summary>The widths an order record's codes are stored at, named once so the record and its columns agree.</summary>
public static class OrderRecordLimits
{
    /// <summary>Twice the longest code <c>CancelReasons</c> or <c>CancelOrigins</c> defines, so a new one fits.</summary>
    public const int MaxCodeLength = 32;
}
