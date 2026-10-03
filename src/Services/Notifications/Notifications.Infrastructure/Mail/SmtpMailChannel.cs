using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using Notifications.Application.Mail;
using Polly;

namespace Notifications.Infrastructure.Mail;

/// <summary>The one place that speaks SMTP, and the platform's first output encoding.</summary>
/// <remarks>
/// A relay's exception can quote the mailbox, in its message or in the server's reply, so none leaves this class:
/// each becomes a <see cref="MailUnavailableException"/> naming the message, the phase and the reply code (§13.4).
/// </remarks>
internal sealed partial class SmtpMailChannel(
    IOptions<MailOptions> options,
    MailPipeline pipeline,
    MailMetrics metrics,
    TimeProvider clock) : IMailChannel
{
    // RFC 5321 section 4.5.3.1.3's path limit, less its angle brackets.
    private const int MaxMailboxLength = 254;

    private static readonly ParserOptions StrictAddresses = new()
    {
        AddressParserComplianceMode = RfcComplianceMode.Strict,
        AllowAddressesWithoutDomain = false
    };

    private enum Phase
    {
        Connecting,
        LoggingIn,
        Sending
    }

    public async Task<MailResult> SendAsync(OutboundMail mail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mail);
        RefuseUnsafeHeaders(mail);

        // Refused before any connection: a line break here would be a header of the customer's choosing.
        if (Mailbox(mail.Recipient) is not { } recipient)
            return new MailResult.Refused(MailRefusal.NotAMailbox);

        using MimeMessage message = Compose(mail, recipient);

        try
        {
            return await pipeline.Pipeline.ExecuteAsync(
                attempt => AttemptAsync(message, mail.MessageId, ct, attempt),
                ct);
        }
        // The pipeline's own refusals, an open circuit or a timeout, carry no word of the relay's.
        catch (ExecutionRejectedException e)
        {
            throw new MailUnavailableException(
                $"Message {mail.MessageId.LocalPart} was not sent: {e.GetType().Name}.",
                MailFault.Transient,
                smtpStatus: null);
        }
    }

    private async ValueTask<MailResult> AttemptAsync(
        MimeMessage message,
        MailMessageId id,
        CancellationToken caller,
        CancellationToken attempt)
    {
        // Validated at start (§15.4), so none of these is null here.
        MailOptions relay = options.Value;
        Phase phase = Phase.Connecting;
        using SmtpClient client = new();

        try
        {
            await client.ConnectAsync(relay.Host!, relay.Port!.Value, Socket(relay.Security), attempt);

            phase = Phase.LoggingIn;
            if (relay.UserName is { Length: > 0 } userName && relay.Password is { Length: > 0 } password)
                await client.AuthenticateAsync(userName, password, attempt);

            phase = Phase.Sending;
            await client.SendAsync(message, attempt);
        }
        // The caller's, or an attempt timeout before the send, which the pipeline converts, counts and may retry.
        catch (OperationCanceledException) when (caller.IsCancellationRequested || phase != Phase.Sending)
        {
            throw;
        }
        catch (SmtpCommandException e)
            when (e.ErrorCode == SmtpErrorCode.RecipientNotAccepted && (int)e.StatusCode >= 500)
        {
            return new MailResult.Refused(MailRefusal.RecipientRefused);
        }
        catch (Exception e)
        {
            (MailFault cause, int? status) = Classify(e, phase);
            metrics.Unavailable(cause);

            string code = status is { } reply ? $" {reply}" : "";
            throw new MailUnavailableException(
                $"Message {id.LocalPart} met {e.GetType().Name}{code} while {Describe(phase)}.",
                cause,
                status);
        }

        await QuitAsync(client, attempt);
        return new MailResult.Accepted();
    }

    private MimeMessage Compose(OutboundMail mail, MailboxAddress recipient)
    {
        // Every header is configuration, a checked value or the clock, so no rendered value reaches one.
        MailboxAddress from = MailboxAddress.Parse(options.Value.From!);
        TextPart body = new(TextFormat.Plain);
        body.SetText(Encoding.UTF8, mail.Body);

        MimeMessage message = new()
        {
            Subject = mail.Subject,
            Date = clock.GetUtcNow(),
            MessageId = $"{mail.MessageId.LocalPart}@{from.Domain}",
            Body = body
        };
        message.From.Add(from);
        message.To.Add(recipient);
        message.Headers.Add(HeaderId.ContentLanguage, string.Join(", ", mail.Languages));

        return message;
    }

    // A subject takes no placeholder and the languages are the deployment's, so either failing is a defect upstream
    // rather than a stranger's input, and it throws rather than refusing a customer.
    private static void RefuseUnsafeHeaders(OutboundMail mail)
    {
        if (string.IsNullOrWhiteSpace(mail.Subject) || mail.Subject.Any(char.IsControl))
            throw new ArgumentException("A subject is one line of text.", nameof(mail));

        if (mail.Languages.Count == 0 || !mail.Languages.All(l => LanguageTag().IsMatch(l)))
            throw new ArgumentException("Each language is a BCP 47 tag.", nameof(mail));
    }

    // The parser's answer compared back to the input, so a display name, a comment or a second address is refused.
    private static MailboxAddress? Mailbox(string recipient) =>
        !string.IsNullOrWhiteSpace(recipient)
        && recipient.Length <= MaxMailboxLength
        && !recipient.Any(char.IsControl)
        && MailboxAddress.TryParse(StrictAddresses, recipient, out MailboxAddress? parsed)
        && parsed is { Name: null or "", Route.Count: 0 }
        && string.Equals(parsed.Address, recipient, StringComparison.Ordinal)
            ? parsed
            : null;

    // No silent fallback: an unbound value is a host the validator should not have started.
    private static SecureSocketOptions Socket(MailSecurity? security) => security switch
    {
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        MailSecurity.None => SecureSocketOptions.None,
        _ => throw new InvalidOperationException($"{MailOptions.SecurityKey} reached the relay unvalidated.")
    };

    // A 4xx is the relay declining for now; a break mid-send leaves the message's fate unknown, so is never retried.
    private static (MailFault Cause, int? Status) Classify(Exception e, Phase phase) => e switch
    {
        SmtpCommandException c when (int)c.StatusCode < 500 => (MailFault.Transient, (int)c.StatusCode),
        SmtpCommandException c when phase == Phase.LoggingIn => (MailFault.Credential, (int)c.StatusCode),
        SmtpCommandException c => (MailFault.Rejected, (int)c.StatusCode),
        SslHandshakeException => (MailFault.Tls, null),
        NotSupportedException when phase == Phase.Connecting => (MailFault.Tls, null),
        AuthenticationException or NotSupportedException when phase == Phase.LoggingIn => (MailFault.Credential, null),
        _ when phase == Phase.Sending => (MailFault.Unconfirmed, null),
        _ => (MailFault.Transient, null)
    };

    private static string Describe(Phase phase) => phase switch
    {
        Phase.Connecting => "connecting",
        Phase.LoggingIn => "logging in",
        _ => "sending"
    };

    // The relay has answered the data with 250, so a QUIT that fails is no failed send.
    private static async Task QuitAsync(SmtpClient client, CancellationToken attempt)
    {
        try
        {
            await client.DisconnectAsync(quit: true, attempt);
        }
        catch (Exception e)
            when (e is IOException or SocketException or SmtpProtocolException or OperationCanceledException)
        {
        }
    }

    [GeneratedRegex("^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageTag();
}
