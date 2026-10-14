using Notifications.Application.Preferences;
using Notifications.Application.Records;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The record's moves, a row per move, and every arrival the row has outgrown as a no-op (ADR-052).</summary>
public class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);
    private static readonly Guid Entry = Guid.CreateVersion7();

    private static Notification Pending(
        Guid? eventId = null,
        Guid? correlationId = null,
        string templateKey = "order-placed",
        Guid? orderId = null,
        string parameters = """{"v":1}""",
        string channel = ConsentVocabulary.Email) =>
        Notification.Pending(
            eventId ?? Guid.CreateVersion7(),
            correlationId ?? Guid.CreateVersion7(),
            templateKey,
            channel,
            orderId ?? Guid.CreateVersion7(),
            parameters,
            Now);

    [Fact]
    public void A_channel_outside_the_vocabulary_is_the_caller_s_defect()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Pending(channel: "sms"));
    }

    [Fact]
    public void A_push_notice_that_fans_out_is_dispatched_with_its_languages_and_entry()
    {
        Notification push = Pending(channel: ConsentVocabulary.Push);

        push.Dispatch("kk,en", Entry, Later).ShouldBeTrue();

        push.Status.ShouldBe(NotificationStatus.Dispatched, "its outcome is now its deliveries'");
        push.Languages.ShouldBe("kk,en");
        push.ConsentEntryId.ShouldBe(Entry, "ADR-120: the row shows the consent the fan-out ran under");
        push.CompletedAt.ShouldBe(Later, "LogRetention ages it from here, once no delivery is pending");
        push.Dispatch("kk,en", Entry, Later).ShouldBeFalse("a dispatched notice is terminal");
        push.MarkUndeliverable(NotificationReasons.NoDevice, Later).ShouldBeFalse();
    }

    [Fact]
    public void A_mail_notice_never_dispatches()
    {
        Notification mail = Pending();

        mail.Dispatch("en", null, Later).ShouldBeFalse("a mail notice is sent, never fanned out");
        mail.Status.ShouldBe(NotificationStatus.Pending);
    }

    [Fact]
    public void A_push_notice_with_no_device_is_undeliverable_and_a_delivery_reason_is_not_a_notice_s()
    {
        Notification push = Pending(channel: ConsentVocabulary.Push);

        Should.Throw<ArgumentOutOfRangeException>(
            () => push.MarkUndeliverable(NotificationReasons.Unregistered, Later),
            "a provider's answer lands on a delivery, never on the notice");
        push.MarkUndeliverable(NotificationReasons.NoDevice, Later).ShouldBeTrue();
        push.Reason.ShouldBe(NotificationReasons.NoDevice);
    }

    private static Notification Started()
    {
        Notification notification = Pending();
        notification.StartSend(1, "en", null, Now).ShouldBeTrue();
        return notification;
    }

    /// <summary>Each move from <c>Pending</c>, with the state it leaves and the reason it stamps.</summary>
    public static TheoryData<string, Func<Notification, bool>, NotificationStatus, string?> Moves => new()
    {
        { "a decline the customer cancelled", n => n.Suppress(Later), NotificationStatus.Suppressed, null },
        {
            "the customer's newest consent entry withdraws the pair",
            n => n.OptOut(Entry, Later),
            NotificationStatus.Suppressed,
            NotificationReasons.OptedOut
        },
        {
            "the owner answers that the customer does not exist",
            n => n.MarkUndeliverable(NotificationReasons.NoSuchCustomer, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.NoSuchCustomer
        },
        {
            "the relay refuses the recipient for good",
            n => n.MarkUndeliverable(NotificationReasons.RecipientRefused, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.RecipientRefused
        },
        {
            "the contact's mailbox is not one a message can be addressed to",
            n => n.MarkUndeliverable(NotificationReasons.NotAMailbox, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.NotAMailbox
        },
        {
            "the give-up age passes",
            n => n.MarkUndeliverable(NotificationReasons.GaveUp, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.GaveUp
        },
        {
            "the customer is erased",
            n => n.MarkUndeliverable(NotificationReasons.Erased, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.Erased
        },
        { "the send is about to start", n => n.StartSend(2, "en,kk", null, Later), NotificationStatus.Pending, null },
    };

    [Fact]
    public void An_event_owes_a_pending_notification_naming_nobody_yet()
    {
        Notification notification = Pending();

        notification.Status.ShouldBe(NotificationStatus.Pending);
        notification.CustomerId.ShouldBeNull("the id is copied from the order record, which may not exist yet");
        notification.SendStartedAt.ShouldBeNull();
        notification.CompletedAt.ShouldBeNull();
        notification.Attempts.ShouldBe(0);
        notification.NextAttemptAt.ShouldBe(Now, "a new row is due at once");
    }

    [Theory]
    [MemberData(nameof(Moves))]
    public void Each_move_from_pending_lands_where_the_record_says(
        string because,
        Func<Notification, bool> move,
        NotificationStatus status,
        string? reason)
    {
        Notification notification = Pending();

        move(notification).ShouldBeTrue(because);

        notification.Status.ShouldBe(status, because);
        notification.Reason.ShouldBe(reason, because);
        if (status == NotificationStatus.Pending)
            notification.CompletedAt.ShouldBeNull(because);
        else
            notification.CompletedAt.ShouldBe(Later, because);
    }

    [Fact]
    public void The_intent_stamps_the_version_and_languages_it_rendered()
    {
        Notification notification = Pending();

        notification.StartSend(2, "en,kk", null, Later).ShouldBeTrue();

        notification.TemplateVersion.ShouldBe(2);
        notification.Languages.ShouldBe("en,kk");
        notification.SendStartedAt.ShouldBe(Later);
    }

    [Fact]
    public void The_intent_stamps_the_consent_entry_it_was_decided_under()
    {
        Notification decided = Pending();
        Notification defaulted = Pending();

        decided.StartSend(1, "en", Entry, Later).ShouldBeTrue();
        defaulted.StartSend(1, "en", null, Later).ShouldBeTrue();

        decided.ConsentEntryId.ShouldBe(Entry, "ADR-120: the record of a send names its entry");
        defaulted.ConsentEntryId.ShouldBeNull("a default or a requirement decided it");
    }

    [Fact]
    public void An_opt_out_names_the_entry_and_never_reaches_a_send_already_under_way()
    {
        Notification optedOut = Pending();
        optedOut.OptOut(Entry, Later).ShouldBeTrue();
        optedOut.ConsentEntryId.ShouldBe(Entry);

        // ADR-120's residual: a send whose intent committed before the withdrawal is still made, and resent.
        Notification started = Started();
        started.OptOut(Entry, Later).ShouldBeFalse();
        started.Status.ShouldBe(NotificationStatus.Pending);
        started.ConsentEntryId.ShouldBeNull();
    }

    [Fact]
    public void The_relay_accepting_a_started_send_makes_it_sent()
    {
        Notification notification = Started();

        notification.MarkSent(Later).ShouldBeTrue();

        notification.Status.ShouldBe(NotificationStatus.Sent);
        notification.CompletedAt.ShouldBe(Later);
    }

    [Fact]
    public void A_send_whose_intent_was_never_committed_is_not_sent()
    {
        // The intent is committed before the send (ADR-052), so an acceptance without one is a pass out of order.
        Notification notification = Pending();

        notification.MarkSent(Later).ShouldBeFalse();

        notification.Status.ShouldBe(NotificationStatus.Pending);
    }

    [Fact]
    public void A_second_intent_keeps_the_first()
    {
        // A row claimed with the intent already set is resent under it, never restamped.
        Notification notification = Started();

        notification.StartSend(3, "ru", null, Later).ShouldBeFalse();

        notification.TemplateVersion.ShouldBe(1);
        notification.Languages.ShouldBe("en");
        notification.SendStartedAt.ShouldBe(Now);
    }

    [Fact]
    public void The_customer_is_named_once()
    {
        Notification notification = Pending();
        Guid first = Guid.CreateVersion7();

        notification.AssignCustomer(first).ShouldBeTrue();
        notification.AssignCustomer(Guid.CreateVersion7()).ShouldBeFalse();

        notification.CustomerId.ShouldBe(first);
    }

    [Theory]
    [MemberData(nameof(Moves))]
    public void Every_arrival_on_a_terminal_row_is_a_no_op_rather_than_a_throw(
        string because,
        Func<Notification, bool> move,
        NotificationStatus status,
        string? reason)
    {
        _ = status;
        _ = reason;

        foreach (Notification terminal in Terminals())
        {
            NotificationStatus before = terminal.Status;
            string? reasonBefore = terminal.Reason;

            move(terminal).ShouldBeFalse(because);
            terminal.MarkSent(Later).ShouldBeFalse(because);
            terminal.AssignCustomer(Guid.CreateVersion7()).ShouldBeFalse(because);

            terminal.Status.ShouldBe(before, because);
            terminal.Reason.ShouldBe(reasonBefore, because);
        }
    }

    [Fact]
    public void A_reason_outside_the_closed_set_is_the_caller_s_defect()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Pending().MarkUndeliverable("relay_down", Later));
    }

    [Fact]
    public void The_closed_set_is_every_terminal_reason_the_record_names()
    {
        // ADR-052's outcomes, the relay's two refusals, §11.7's erasure and a push notice with no device, and nothing a
        // caller could misspell.
        NotificationReasons.All.ShouldBe(
            ["no_such_customer", "recipient_refused", "not_a_mailbox", "gave_up", "erased", "no_device"],
            ignoreOrder: true);
        NotificationReasons.Delivery.ShouldBe(
            ["unregistered", "invalid_token", "no_device", "gave_up"],
            ignoreOrder: true);
    }

    [Fact]
    public void An_opt_out_is_a_suppression_and_never_an_undeliverable_reason()
    {
        // ADR-120's opted_out ends a row Suppressed; MarkUndeliverable refuses it as the typo it would be there.
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Pending().MarkUndeliverable(NotificationReasons.OptedOut, Later));
    }

    [Fact]
    public void An_empty_event_correlation_or_order_id_is_refused_at_the_door()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Pending(eventId: Guid.Empty));
        Should.Throw<ArgumentOutOfRangeException>(() => Pending(correlationId: Guid.Empty));
        Should.Throw<ArgumentOutOfRangeException>(() => Pending(orderId: Guid.Empty));
    }

    [Fact]
    public void An_empty_customer_id_is_refused_for_the_customer_is_named_once()
    {
        Notification notification = Pending();

        Should.Throw<ArgumentOutOfRangeException>(() => notification.AssignCustomer(Guid.Empty));

        notification.CustomerId.ShouldBeNull();
    }

    [Fact]
    public void A_value_the_columns_cannot_hold_is_refused_at_the_door()
    {
        Should.Throw<ArgumentException>(() => Pending(templateKey: " "));
        Should.Throw<ArgumentException>(() => Pending(parameters: ""));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Pending(templateKey: new string('k', NotificationLimits.MaxTemplateKeyLength + 1)));
        Should.Throw<ArgumentOutOfRangeException>(() => Pending().StartSend(0, "en", null, Later));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Pending().StartSend(1, new string('x', NotificationLimits.MaxLanguagesLength + 1), null, Later));
    }

    private static IEnumerable<Notification> Terminals()
    {
        Notification suppressed = Pending();
        suppressed.Suppress(Now);

        Notification undeliverable = Pending();
        undeliverable.MarkUndeliverable(NotificationReasons.GaveUp, Now);

        Notification sent = Started();
        sent.MarkSent(Now);

        Notification optedOut = Pending();
        optedOut.OptOut(Entry, Now);

        return [suppressed, undeliverable, sent, optedOut];
    }
}
