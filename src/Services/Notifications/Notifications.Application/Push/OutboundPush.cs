namespace Notifications.Application.Push;

/// <summary>One push as the worker hands it over: rendered, addressed to one device and identified.</summary>
/// <remarks>
/// Its text names the delivery alone, so no log or fault that prints one holds the token (ADR-117, §13.4); the
/// collapse key makes a resend replace the copy it repeats on the device rather than show beside it.
/// </remarks>
public sealed record OutboundPush(
    string Platform,
    string AppId,
    string Token,
    string Title,
    string Body,
    Guid OrderId,
    string TemplateKey,
    string CollapseKey,
    Guid DeliveryId,
    Guid CorrelationId,
    DateTimeOffset GiveUpAt)
{
    public override string ToString() => $"{nameof(OutboundPush)} {{ {nameof(DeliveryId)} = {DeliveryId} }}";
}
