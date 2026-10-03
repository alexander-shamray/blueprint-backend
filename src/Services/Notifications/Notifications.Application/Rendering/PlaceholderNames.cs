namespace Notifications.Application.Rendering;

/// <summary>Every name a template may write as <c>{Name}</c>; <see cref="TemplateKeys"/> says which.</summary>
public static class PlaceholderNames
{
    public const string OrderId = "OrderId";

    /// <summary>The event's instant, as a date in the deployment's zone and the language's culture (ADR-053).</summary>
    public const string Date = "Date";

    /// <summary>The event's decimal in the language's culture at its own scale, never rounded (ADR-053).</summary>
    public const string Amount = "Amount";

    public const string Currency = "Currency";

    public const string TrackingNumber = "TrackingNumber";

    /// <summary>The cancellation's phrase from its version's map, never the code itself.</summary>
    public const string CancelReason = "CancelReason";
}
