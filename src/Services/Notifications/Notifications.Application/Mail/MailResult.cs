namespace Notifications.Application.Mail;

/// <summary>The relay's answer to an <see cref="OutboundMail"/>.</summary>
public abstract record MailResult
{
    private MailResult() { }

    public sealed record Accepted : MailResult;

    public sealed record Refused(MailRefusal Reason) : MailResult;
}
