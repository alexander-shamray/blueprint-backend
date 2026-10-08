namespace Notifications.Application.Mail;

/// <summary>Why a send is over for good, each a terminal reason of its own on the row.</summary>
public enum MailRefusal
{
    /// <summary>The relay refused the recipient's address or mailbox itself, for good, rather than this deployment.</summary>
    RecipientRefused,

    /// <summary>The recipient is not one bare mailbox, a line break in it above all, so nothing was sent.</summary>
    NotAMailbox
}
