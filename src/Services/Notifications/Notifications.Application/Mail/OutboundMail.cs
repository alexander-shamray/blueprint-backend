namespace Notifications.Application.Mail;

/// <summary>One message as the worker hands it over: rendered, addressed and identified.</summary>
/// <remarks>Its text names the message alone, so a log or a fault that prints one holds no mailbox (§13.4).</remarks>
public sealed record OutboundMail(
    string Recipient,
    string Subject,
    string Body,
    MailMessageId MessageId,
    IReadOnlyList<string> Languages)
{
    public override string ToString() => $"{nameof(OutboundMail)} {{ {nameof(MessageId)} = {MessageId.LocalPart} }}";
}
