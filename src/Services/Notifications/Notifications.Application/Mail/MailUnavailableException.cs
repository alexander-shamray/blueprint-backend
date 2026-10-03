namespace Notifications.Application.Mail;

/// <summary>A send that met a fault rather than an answer: the row backs off, and nothing reaches a queue.</summary>
public sealed class MailUnavailableException : Exception
{
    public MailUnavailableException()
    {
    }

    public MailUnavailableException(string message)
        : base(message)
    {
    }

    public MailUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The adapter's form, with no inner exception, as a relay's own can quote the mailbox.</summary>
    public MailUnavailableException(string message, MailFault cause, int? smtpStatus)
        : base(message)
    {
        Cause = cause;
        SmtpStatus = smtpStatus;
    }

    public MailFault Cause { get; }

    /// <summary>The relay's reply code, where it gave one; its words are never kept.</summary>
    public int? SmtpStatus { get; }
}
