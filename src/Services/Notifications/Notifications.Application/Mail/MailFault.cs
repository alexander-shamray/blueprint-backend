namespace Notifications.Application.Mail;

/// <summary>Why a send met a fault rather than an answer, which decides its retry and its count.</summary>
public enum MailFault
{
    /// <summary>The relay never took the message: a <c>4xx</c>, a refused connection, an early timeout.</summary>
    Transient,

    /// <summary>A break or a timeout once the send began, so the relay may hold the message (§9.7).</summary>
    Unconfirmed,

    /// <summary>A session weaker than configured, which is somebody's decision rather than an outage.</summary>
    Tls,

    /// <summary>The relay refused this host's credential, a deployment's fault and not the customer's.</summary>
    Credential,

    /// <summary>A permanent refusal of the sender or the message rather than of the recipient.</summary>
    Rejected
}
