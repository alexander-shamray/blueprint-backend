namespace Notifications.Application.Mail;

/// <summary>Why a send is over for good, each a terminal reason of its own on the row.</summary>
public enum MailRefusal
{
    /// <summary>The relay answered the recipient with a permanent <c>5xx</c>.</summary>
    RecipientRefused,

    /// <summary>The recipient is not one bare mailbox, a line break in it above all, so nothing was sent.</summary>
    NotAMailbox
}
