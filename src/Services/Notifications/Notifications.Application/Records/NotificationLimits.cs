namespace Notifications.Application.Records;

/// <summary>The widths a notification's strings are stored at, named once so the record and columns agree.</summary>
public static class NotificationLimits
{
    public const int MaxTemplateKeyLength = 64;
    public const int MaxLanguagesLength = 32;
    public const int MaxReasonLength = 32;
    public const int MaxParametersLength = 2000;
}
