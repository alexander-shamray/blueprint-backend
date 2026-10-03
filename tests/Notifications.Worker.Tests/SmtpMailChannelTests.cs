using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay's rows that end in an answer, over one host and a real relay (§12.7).</summary>
[Collection(nameof(MailpitCollection))]
public sealed class SmtpMailChannelTests(MailpitFixture fixture) : IAsyncLifetime
{
    private const string Customer = "aigerim@example.test";

    public async ValueTask InitializeAsync() => await fixture.Plain.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IMailChannel Channel() => fixture.Host.Services.GetRequiredService<IMailChannel>();

    private static OutboundMail Mail(
        string recipient = Customer,
        string subject = "Your order is placed",
        string body = "Order 42 is placed.",
        MailMessageId? id = null,
        IReadOnlyList<string>? languages = null) =>
        new(
            recipient,
            subject,
            body,
            id ?? new MailMessageId(Guid.CreateVersion7(), "order-placed"),
            languages ?? ["en"]);

    [Fact]
    public async Task A_message_arrives_as_plain_utf8_text_under_the_message_id_it_was_given()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        MailMessageId id = new(Guid.CreateVersion7(), "order-placed");

        MailResult result = await Channel().SendAsync(Mail(id: id, languages: ["kk", "ru"]), ct);

        result.ShouldBe(new MailResult.Accepted());
        MailpitMessage arrived = await fixture.Plain.SingleAsync(ct);
        arrived.MessageId.Trim('<', '>').ShouldBe($"{id.LocalPart}@commerce.test");
        arrived.To.ShouldHaveSingleItem().Address.ShouldBe(Customer);
        arrived.From.Address.ShouldBe("no-reply@commerce.test");

        IReadOnlyDictionary<string, string[]> headers = await fixture.Plain.HeadersAsync(arrived.Id, ct);
        headers["Content-Type"].ShouldHaveSingleItem()
            .ShouldBe("text/plain; charset=utf-8", StringCompareShould.IgnoreCase);
        headers["Content-Language"].ShouldHaveSingleItem().ShouldBe("kk, ru");
    }

    [Fact]
    public async Task A_Kazakh_subject_body_and_sender_name_arrive_intact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string subject = "Тапсырысыңыз қабылданды: ә ғ қ ң ө ұ ү һ і";
        const string body = "Сәлеметсіз бе!\nТапсырыс қабылданды: Ә Ғ Қ Ң Ө Ұ Ү Һ І.\nРақмет.";

        // A host of its own, because the sender is configuration and this one is named in Kazakh too.
        using NotificationsWorkerFactory host = MailpitFixture.Development(
            fixture.Plain,
            from: "Дүкен <no-reply@commerce.test>");

        MailResult result = await host.Services.GetRequiredService<IMailChannel>()
            .SendAsync(Mail(subject: subject, body: body, languages: ["kk"]), ct);

        result.ShouldBe(new MailResult.Accepted());
        MailpitMessage arrived = await fixture.Plain.SingleAsync(ct);
        arrived.Subject.ShouldBe(subject);
        arrived.From.Name.ShouldBe("Дүкен");

        // SMTP's line ending is CRLF whatever the body's was (RFC 5321 section 2.3.8), so lines are compared.
        arrived.Text.ReplaceLineEndings("\n").TrimEnd('\n').ShouldBe(body);
    }

    [Theory]
    [InlineData("aigerim@example.test\r\nBcc: someone@example.test")]
    [InlineData("aigerim@example.test\n")]
    [InlineData("aigerim\r@example.test")]
    public async Task A_mailbox_carrying_a_line_break_is_refused_and_nothing_is_sent(string recipient)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        MailResult result = await Channel().SendAsync(Mail(recipient: recipient), ct);

        result.ShouldBe(new MailResult.Refused(MailRefusal.NotAMailbox));
        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Aigerim <aigerim@example.test>")]
    [InlineData("aigerim@example.test, someone@example.test")]
    [InlineData("aigerim@example.test (a comment)")]
    [InlineData("aigerim")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_recipient_that_is_not_one_bare_mailbox_is_refused(string recipient)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        MailResult result = await Channel().SendAsync(Mail(recipient: recipient), ct);

        result.ShouldBe(new MailResult.Refused(MailRefusal.NotAMailbox));
        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_subject_carrying_a_line_break_is_a_defect_and_nothing_is_sent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<ArgumentException>(() =>
            Channel().SendAsync(Mail(subject: "Placed\r\nBcc: someone@example.test"), ct));

        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("en\r\nBcc: someone@example.test")]
    [InlineData("english")]
    [InlineData("")]
    public async Task A_language_that_is_not_a_tag_is_a_defect_and_nothing_is_sent(string language)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<ArgumentException>(() => Channel().SendAsync(Mail(languages: [language]), ct));

        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_recipient_the_relay_refuses_for_good_is_an_answer_and_is_not_counted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(550, ct);
        using MailCount counted = MailCounter.Unavailable(fixture.Host.Services);

        MailResult result = await Channel().SendAsync(Mail(), ct);

        result.ShouldBe(new MailResult.Refused(MailRefusal.RecipientRefused));
        counted.Value.ShouldBe(0, "a refusal is an answer, so the pipeline neither retries it nor counts it");
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_counted_against_the_relay()
    {
        using MailCount counted = MailCounter.Unavailable(fixture.Host.Services);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Channel().SendAsync(Mail(), cancelled.Token));

        counted.Value.ShouldBe(0, "a pass cancelled at shutdown is not a relay incident");
    }
}
