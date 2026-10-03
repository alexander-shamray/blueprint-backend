namespace Notifications.Infrastructure.Mail;

/// <summary>How the session to the relay is protected; <see cref="None"/> is Development's alone (§15.4).</summary>
public enum MailSecurity
{
    None,
    StartTls
}
