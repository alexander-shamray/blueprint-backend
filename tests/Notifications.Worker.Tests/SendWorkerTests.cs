using Common.Contracts.Ordering.V1;
using Common.Web;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The send pass's steps over the real tables, a real relay and a stub Keycloak (ADR-049, ADR-052).</summary>
/// <remarks>One shared host, as no case here fills a breaker: a refusal and a 404 are answers to it (§9.7).</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class SendWorkerTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan GiveUpAge = TimeSpan.Parse(
        NotificationsWorkerFactory.InventedGiveUpAge,
        System.Globalization.CultureInfo.InvariantCulture);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_placed_order_is_sent_in_the_contact_s_language_under_its_event_s_message_id()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en-GB");
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.WaitUntilDueAsync(order);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification sent = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        sent.Status.ShouldBe(NotificationStatus.Sent);
        sent.CustomerId.ShouldBe(customer, "copied from the order record (§11.7's erasure replaces it)");
        sent.TemplateVersion.ShouldBe(1);
        sent.Languages.ShouldBe("en", "en-GB's primary subtag is in the invented set, so it alone is sent");
        sent.SendStartedAt.ShouldNotBeNull("the intent was committed before the send");
        sent.CompletedAt.ShouldNotBeNull();

        MailpitMessage message = await fixture.Relay.SingleAsync(Ct);
        message.MessageId.Trim('<', '>').ShouldBe($"{sent.EventId:N}.{TemplateKeys.OrderPlaced}@commerce.test");
        message.To.ShouldHaveSingleItem().Address.ShouldBe(Mailbox);
        (await fixture.ContactAsync(customer))!.Email.ShouldBe(Mailbox, "the owner's answer is kept (ADR-052)");
    }

    [Fact]
    public async Task A_sent_message_carries_its_event_s_correlation_id_and_nothing_else_in_that_header()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en-GB");

        // Apart from the order id its publisher sets it to, so the header is shown to be the event's own (§10.4).
        Guid correlation = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At) with { CorrelationId = correlation });
        await fixture.WaitUntilDueAsync(order);

        await fixture.RunSendPassAsync();

        MailpitMessage message = await fixture.Relay.SingleAsync(Ct);
        (await fixture.Relay.HeadersAsync(message.Id, Ct))["X-Correlation-Id"]
            .ShouldHaveSingleItem().ShouldBe(correlation.ToString("D"));
    }

    [Fact]
    public async Task A_row_a_version_before_the_column_writes_is_sent_under_its_order_as_its_correlation_id()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        Notification owed = await OwedAsync(order, customer);

        // The column's default, which an insert that does not name the column leaves on the row (§7.4).
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET CorrelationId = {1} WHERE NotificationId = {0};",
            owed.NotificationId,
            Guid.Empty);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        MailpitMessage message = await fixture.Relay.SingleAsync(Ct);
        (await fixture.Relay.HeadersAsync(message.Id, Ct))["X-Correlation-Id"]
            .ShouldHaveSingleItem().ShouldBe(order.ToString("D"));
    }

    [Fact]
    public void The_mail_header_is_the_one_every_host_reads_its_correlation_id_from() =>
        SmtpMailChannel.CorrelationIdHeader.ShouldBe(CorrelationIdExtensions.Header);

    [Fact]
    public async Task A_customer_with_no_locale_is_sent_every_language_of_the_set_in_its_order()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox);
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.WaitUntilDueAsync(order);

        await fixture.RunSendPassAsync();

        (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem().Languages.ShouldBe("kk,en");
        MailpitMessage message = await fixture.Relay.SingleAsync(Ct);
        message.Subject.ShouldContain(TemplateRenderer.SubjectSeparator);
        (await fixture.Relay.HeadersAsync(message.Id, Ct))["Content-Language"]
            .ShouldHaveSingleItem().ShouldBe("kk, en");
    }

    [Fact]
    public async Task A_notice_that_arrives_before_its_order_waits_then_sends_once_the_record_exists()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At));
        await fixture.WaitUntilDueAsync(order);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));

        Notification waiting = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1, "a wait backs off on the row's own count");
        waiting.CustomerId.ShouldBeNull();
        fixture.ContactCalls(customer).ShouldBe(0, "nothing is asked of the owner before the customer is known");

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.SendUntilSettledAsync();

        IReadOnlyList<Notification> rows = await fixture.NotificationsAsync(order);
        rows.ShouldAllBe(n => n.Status == NotificationStatus.Sent && n.CustomerId == customer);
        (await fixture.Relay.WaitForAsync(2, Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_decline_before_its_cancellation_waits_and_the_customer_s_cancellation_suppresses_it()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Declined(order, At));
        await fixture.WaitUntilDueAsync(order);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(2, 1), "the placement sends and the decline waits");
        Notification decline = await DeclineAsync(order);
        decline.Status.ShouldBe(NotificationStatus.Pending);
        decline.Attempts.ShouldBe(1);

        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At, CancelReasons.CustomerRequest, CancelOrigins.User));
        await fixture.SendUntilSettledAsync();

        decline = await DeclineAsync(order);
        decline.Status.ShouldBe(NotificationStatus.Suppressed, "ADR-049: a customer who cancelled is never told");
        decline.CustomerId.ShouldBe(customer);
        decline.SendStartedAt.ShouldBeNull("nothing was rendered or sent");

        IReadOnlyList<MailpitSummary> delivered = await fixture.Relay.WaitForAsync(2, Ct);
        delivered.ShouldNotContain(m => m.MessageId.Contains(TemplateKeys.PaymentDeclined, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_decline_the_saga_cancelled_is_sent_beside_its_cancellation()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At, CancelReasons.PaymentDeclined, CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Declined(order, At));

        await fixture.SendUntilSettledAsync();

        (await fixture.NotificationsAsync(order)).ShouldAllBe(n => n.Status == NotificationStatus.Sent);
        (await fixture.Relay.WaitForAsync(3, Ct))
            .ShouldContain(m => m.MessageId.Contains(TemplateKeys.PaymentDeclined, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CancelReasons.PaymentTimeout, null)]
    [InlineData(CancelReasons.PaymentDeclined, null)]
    [InlineData(CancelReasons.PaymentDeclined, "system")]
    [InlineData(CancelReasons.CustomerRequest, "system")]
    public async Task A_cancellation_with_no_known_origin_suppresses_the_decline_whatever_its_reason(
        string reason,
        string? origin)
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Cancelled(order, customer, At, reason, origin));
        await fixture.DeliverAsync(OrderEvents.Declined(order, At));

        await fixture.SendUntilSettledAsync();

        (await DeclineAsync(order)).Status.ShouldBe(NotificationStatus.Suppressed, "only the workflow's sends it");
    }

    [Fact]
    public async Task A_fresh_contact_is_served_without_asking_the_owner()
    {
        // No stub mapping: an owner asked would answer a 404 that names no user, and ContactCalls would count it.
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromMinutes(1));
        Notification owed = await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Sent);
        fixture.ContactCalls(customer).ShouldBe(0);
    }

    [Fact]
    public async Task A_stale_contact_is_refreshed_from_an_owner_that_answers()
    {
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, "old@example.test", "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, Mailbox, "en");
        await OwedAsync(order, customer);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        await fixture.RunSendPassAsync();

        (await fixture.Relay.SingleAsync(Ct)).To.ShouldHaveSingleItem().Address.ShouldBe(Mailbox);
        ContactRecord refreshed = (await fixture.ContactAsync(customer)).ShouldNotBeNull();
        refreshed.Email.ShouldBe(Mailbox);
        refreshed.FetchedAt.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
    }

    [Fact]
    public async Task A_customer_owed_three_notices_in_one_pass_is_asked_for_once()
    {
        // The owner stalls, so every row reaches the empty table before any answer is kept there.
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en", delay: TimeSpan.FromMilliseconds(500));
        Guid[] orders = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()];

        foreach (Guid order in orders)
            await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(3, 3));

        fixture.ContactCalls(customer).ShouldBe(1, "ADR-052 resolves a customer once per pass");
        (await fixture.Relay.WaitForAsync(3, Ct)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_customer_the_owner_does_not_know_is_undeliverable_and_their_contact_row_goes()
    {
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, 404);
        Notification owed = await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification ended = await fixture.NotificationAsync(owed.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.NoSuchCustomer);
        (await fixture.ContactAsync(customer)).ShouldBeNull("ADR-052's fifth row deletes the contact row");
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
        (await fixture.RunSendPassAsync()).Claimed.ShouldBe(0, "a terminal row is not retried");
    }

    [Fact]
    public async Task A_recipient_the_relay_refuses_for_good_is_undeliverable_and_not_retried()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.Relay.RefuseRecipientsAsync(550, Ct);
        Notification owed = await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification ended = await fixture.NotificationAsync(owed.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.RecipientRefused);
        (await fixture.RunSendPassAsync()).Claimed.ShouldBe(0);
    }

    [Fact]
    public async Task A_mailbox_carrying_a_line_break_is_not_a_mailbox_and_nothing_is_sent()
    {
        // The owner answers it as stored, as ADR-052 has no outcome for a malformed one; the channel judges a mailbox.
        const string broken = "aigerim@example.test\r\nBcc: someone@example.test";
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, broken, "en");
        Notification owed = await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification ended = await fixture.NotificationAsync(owed.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.NotAMailbox);
        (await fixture.ContactAsync(customer))!.Email.ShouldBe(broken, "stored as the owner gave it");
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notice_pending_past_its_give_up_age_is_given_up_without_asking_anyone()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        Notification owed = await OwedAsync(order, customer);
        await fixture.AgeAsync(owed.NotificationId, GiveUpAge + TimeSpan.FromMinutes(1));

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification ended = await fixture.NotificationAsync(owed.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.GaveUp);
        fixture.ContactCalls(customer).ShouldBe(0, "the age is asked before anyone is");
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
        (await fixture.RunSendPassAsync()).Claimed.ShouldBe(0, "a give-up is terminal and not retried");
    }

    [Fact]
    public async Task A_notice_inside_its_give_up_age_still_waits()
    {
        Guid order = Guid.CreateVersion7();
        Notification owed = await fixture.PendingAsync(TemplateKeys.ShipmentDelivered, order);
        await fixture.AgeAsync(owed.NotificationId, GiveUpAge - TimeSpan.FromHours(1));

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));

        Notification waiting = await fixture.NotificationAsync(owed.NotificationId);
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Parameters_this_version_cannot_read_back_the_row_off_and_fault_nothing()
    {
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromMinutes(1));
        await fixture.OrderAsync(order, customer);
        Notification owed = await fixture.PendingAsync(
            TemplateKeys.OrderConfirmed,
            order,
            storedParameters: $$"""{"v":2,"orderId":"{{order:D}}","occurredAt":"2026-10-02T09:00:00+00:00"}""");

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));

        Notification waiting = await fixture.NotificationAsync(owed.NotificationId);
        waiting.Status.ShouldBe(NotificationStatus.Pending, "a newer version's replica can still send it");
        waiting.Attempts.ShouldBe(1);
        waiting.SendStartedAt.ShouldBeNull();
        fixture.CapturedLogs.Everything.ShouldContain(
            line => line.Contains("cannot read", StringComparison.Ordinal) &&
                line.Contains(owed.NotificationId.ToString(), StringComparison.Ordinal));
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lapsed_lease_is_taken_by_another_pass()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        Notification owed = await OwedAsync(order, customer);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET LockedUntil = DATEADD(second, -1, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            owed.NotificationId);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1), "a lease in the past is no lease");
    }

    [Fact]
    public async Task Two_workers_overlapping_claim_one_row_once()
    {
        // The owner stalls short of ContactHop's attempt timeout, so the first pass holds the lease meanwhile.
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en", delay: TimeSpan.FromMilliseconds(800));
        Notification owed = await OwedAsync(order, customer);
        using NotificationsWorkerFactory second = fixture.NewWorkerHost();
        // Resolved first, as the host starts on first use and must not spend the stall starting.
        SendWorker other = second.Services.GetRequiredService<SendWorker>();

        Task<SendPass> first = fixture.RunSendPassAsync();
        await ServiceFixture.WaitUntilAsync(async () => await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
            "AND Status = 'Pending' AND LockedUntil > SYSDATETIMEOFFSET()",
            owed.NotificationId) == 1);

        (await other.RunOnceAsync(Ct)).ShouldBe(new SendPass(0, 0), "the second worker skipped a leased row");
        (await first).ShouldBe(new SendPass(1, 1));

        (await fixture.NotificationAsync(owed.NotificationId)).Attempts.ShouldBe(0);
        (await fixture.Relay.SingleAsync(Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_crash_between_the_relay_s_accept_and_the_commit_sends_twice_under_one_message_id_and_counts_it()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        Notification owed = await OwedAsync(order, customer);
        using OutboundCount resent = ResentCounter.Resent(fixture.Factory.Services);

        using (CommitFault fault = fixture.FailNextSentCommit())
        {
            // The relay accepted; the commit failed, which the per-row catch backs off rather than throws.
            (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));
            fault.Fired.ShouldBeTrue();
        }

        Notification crashed = await fixture.NotificationAsync(owed.NotificationId);
        crashed.Status.ShouldBe(NotificationStatus.Pending);
        crashed.SendStartedAt.ShouldNotBeNull("the intent precedes the send, so the row says it may be out");

        // The contact now picks another language, so a fresh render would differ from the stamped one.
        await fixture.StageContactAsync(customer, Mailbox, "kk", TimeSpan.FromMinutes(1));
        await fixture.ClearBackoffAsync(owed.NotificationId);
        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        IReadOnlyList<MailpitSummary> delivered = await fixture.Relay.WaitForAsync(2, Ct);
        delivered.Select(m => m.MessageId.Trim('<', '>')).Distinct().ShouldHaveSingleItem()
            .ShouldBe($"{owed.EventId:N}.{TemplateKeys.OrderConfirmed}@commerce.test");
        delivered.Select(m => m.Subject).Distinct().ShouldHaveSingleItem("a resend renders the stamped text");
        (await Task.WhenAll(delivered.Select(m => fixture.Relay.HeadersAsync(m.Id, Ct))))
            .Select(h => h["Content-Language"].Single()).Distinct()
            .ShouldHaveSingleItem("a resend keeps the stamped language")
            .ShouldBe("en");

        Notification sent = await fixture.NotificationAsync(owed.NotificationId);
        sent.Status.ShouldBe(NotificationStatus.Sent);
        sent.SendStartedAt.ShouldBe(crashed.SendStartedAt, "a second start keeps the first stamp");
        sent.Languages.ShouldBe("en", "the stamp outlives the contact's new locale");
        resent.Value.ShouldBe(1);
    }

    [Fact]
    public async Task A_pass_that_throws_leaves_the_host_running()
    {
        // The claim failing, not a row, as the pass catches per row; an unreachable database makes the pass throw.
        using NotificationsWorkerFactory broken = new(Unreachable.Sql, Unreachable.Rabbit);
        SendWorker worker = broken.Services.GetRequiredService<SendWorker>();

        await Should.ThrowAsync<Exception>(() => worker.RunOnceAsync(Ct));

        await worker.StartAsync(Ct);

        // Staged on the loop's own line, which the direct call above never logs.
        await ServiceFixture.WaitUntilAsync(() =>
            Task.FromResult(ClaimFailedLogged(broken) || worker.ExecuteTask!.IsCompleted));

        // ExecuteTask is the loop, and a faulted one is the host on its way down.
        worker.ExecuteTask!.IsFaulted.ShouldBeFalse();
        ClaimFailedLogged(broken).ShouldBeTrue();

        await worker.StopAsync(Ct);
    }

    [Fact]
    public async Task A_host_stopped_mid_pass_finishes_the_send_and_commits_it_once()
    {
        // The owner stalls, so the stop lands inside the pass, ahead of the send and the commit the drain lets run.
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en", delay: TimeSpan.FromMilliseconds(800));
        Notification owed = await OwedAsync(order, customer);
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        using OutboundCount resent = ResentCounter.Resent(host.Services);
        SendWorker worker = host.Services.GetRequiredService<SendWorker>();

        await worker.StartAsync(Ct);
        await ServiceFixture.WaitUntilAsync(async () => await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
            "AND Status = 'Pending' AND LockedUntil > SYSDATETIMEOFFSET()",
            owed.NotificationId) == 1);
        await worker.StopAsync(Ct);

        Notification sent = await fixture.NotificationAsync(owed.NotificationId);
        sent.Status.ShouldBe(NotificationStatus.Sent, "a stop drains the pass under way rather than cancel it");
        sent.Attempts.ShouldBe(0);
        (await fixture.Relay.SingleAsync(Ct)).ShouldNotBeNull();
        resent.Value.ShouldBe(0, "nothing is left for a later pass to send again");
    }

    private static (Guid Order, Guid Customer) Ids() => (Guid.CreateVersion7(), Guid.CreateVersion7());

    private static bool ClaimFailedLogged(NotificationsWorkerFactory host) =>
        host.CapturedLogs.Everything.Any(line => line.Contains("Send claim failed", StringComparison.Ordinal));

    /// <summary>A confirmed order's notice with its record made, so a pass meets nothing to wait on.</summary>
    private async Task<Notification> OwedAsync(Guid order, Guid customer)
    {
        await fixture.OrderAsync(order, customer);

        return await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
    }

    private async Task<Notification> DeclineAsync(Guid order) =>
        (await fixture.NotificationsAsync(order)).Single(n => n.TemplateKey == TemplateKeys.PaymentDeclined);
}
