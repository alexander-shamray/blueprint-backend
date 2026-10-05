using Notifications.Application.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

public class MailPortTests
{
    [Fact]
    public void A_send_is_accepted_or_refused_and_nothing_else()
    {
        typeof(MailResult).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(MailResult)))
            .Select(t => t.Name)
            .ShouldBe(["Accepted", "Refused"], ignoreOrder: true,
                "a fault is an exception, so a dead relay can never reach a row as a refusal");
    }

    [Fact]
    public void A_refusal_is_the_relays_or_the_mailboxs_and_each_is_its_own_terminal_reason()
    {
        Enum.GetNames<MailRefusal>().ShouldBe(["RecipientRefused", "NotAMailbox"], ignoreOrder: true);
    }

    [Fact]
    public void A_fault_is_one_of_five_and_the_names_are_the_counters_vocabulary()
    {
        Enum.GetNames<MailFault>()
            .ShouldBe(["Transient", "Unconfirmed", "Tls", "Credential", "Rejected"], ignoreOrder: true);
    }

    [Fact]
    public void The_message_id_is_the_event_and_the_template_key()
    {
        Guid eventId = Guid.CreateVersion7();

        new MailMessageId(eventId, "order-placed").LocalPart.ShouldBe($"{eventId:N}.order-placed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Order-Placed")]
    [InlineData("order placed")]
    [InlineData("order-placed\r\nBcc: someone@example.test")]
    [InlineData("order@placed")]
    [InlineData("-order")]
    [InlineData("order-")]
    public void A_template_key_that_is_not_kebab_case_never_reaches_a_header(string key)
    {
        Should.Throw<ArgumentException>(() => new MailMessageId(Guid.CreateVersion7(), key));
    }

    [Fact]
    public void An_empty_event_id_is_refused()
    {
        Should.Throw<ArgumentException>(() => new MailMessageId(Guid.Empty, "order-placed"));
    }

    [Fact]
    public void A_mail_prints_its_message_id_and_neither_its_mailbox_nor_its_body()
    {
        OutboundMail mail = new(
            "aigerim@example.test",
            "Your order is placed",
            "Order 42 is placed.",
            new MailMessageId(Guid.CreateVersion7(), "order-placed"),
            Guid.CreateVersion7(),
            ["kk"]);

        string printed = mail.ToString();

        printed.ShouldContain(mail.MessageId.LocalPart);
        printed.ShouldNotContain("aigerim", Case.Insensitive, "§13.4: a log takes the ids, never the mailbox");
        printed.ShouldNotContain("Order 42", Case.Insensitive, "nor the body");
    }

    [Fact]
    public void An_unavailable_relay_carries_its_cause_and_its_code_and_nothing_of_the_relays_own()
    {
        MailUnavailableException thrown = new(
            "Message x met SmtpCommandException 451 while sending.", MailFault.Transient, 451);

        thrown.Cause.ShouldBe(MailFault.Transient);
        thrown.SmtpStatus.ShouldBe(451);
        thrown.InnerException.ShouldBeNull("a relay's own exception can quote the mailbox");
    }
}
