namespace Notifications.Application.Mail;

/// <summary>The relay in this service's words, and the one place a vendor's adapter would go (§3.1).</summary>
/// <remarks>
/// A fault throws <see cref="MailUnavailableException"/> and is never a <see cref="MailResult"/>, so a dead relay
/// cannot reach a row as a refusal; no SMTP type reaches above this port.
/// </remarks>
public interface IMailChannel
{
    Task<MailResult> SendAsync(OutboundMail mail, CancellationToken ct);
}
