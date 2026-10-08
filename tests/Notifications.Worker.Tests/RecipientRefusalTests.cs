using MailKit.Net.Smtp;
using Notifications.Infrastructure.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>Which permanent answers to RCPT TO end a row as the customer's, and which are the relay's.</summary>
public sealed class RecipientRefusalTests
{
    [Theory]
    [InlineData(550, "5.1.1 <a@example.test>: Recipient address rejected: User unknown")]
    [InlineData(553, "5.1.3 Bad recipient address syntax")]
    [InlineData(554, "5.1.1 No such user")]
    [InlineData(550, "Requested action not taken: mailbox unavailable")]
    [InlineData(551, "User not local")]
    [InlineData(553, "Requested action not taken: mailbox name not allowed")]
    public void A_refusal_of_the_mailbox_is_the_customers(int status, string reply) =>
        SmtpMailChannel.RefusesTheMailbox(Recipient(status, reply)).ShouldBeTrue(reply);

    [Theory]
    [InlineData(554, "5.7.1 <a@example.test>: Relay access denied")]
    [InlineData(550, "5.7.1 Unable to relay")]
    [InlineData(550, "5.7.54 SMTP; Unable to relay recipient in non-accepted domain")]
    [InlineData(554, "Transaction failed")]
    [InlineData(552, "Requested mail action aborted: exceeded storage allocation")]
    public void A_refusal_of_this_deployment_is_not(int status, string reply) =>
        SmtpMailChannel.RefusesTheMailbox(Recipient(status, reply)).ShouldBeFalse(reply);

    [Fact]
    public void A_temporary_answer_or_another_command_is_never_a_refusal_of_the_mailbox()
    {
        SmtpMailChannel.RefusesTheMailbox(Recipient(450, "4.1.1 Try again later")).ShouldBeFalse();
        SmtpMailChannel
            .RefusesTheMailbox(new SmtpCommandException(
                SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxUnavailable, "5.1.1 Sender unknown"))
            .ShouldBeFalse();
    }

    private static SmtpCommandException Recipient(int status, string reply) =>
        new(SmtpErrorCode.RecipientNotAccepted, (SmtpStatusCode)status, reply);
}
