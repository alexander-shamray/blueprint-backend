using Notifications.Application.Records;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The record's moves, a row per move, and every arrival the row has outgrown as a no-op (ADR-052).</summary>
public class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static Notification Pending() =>
        Notification.Pending(Guid.CreateVersion7(), "order-placed", Guid.CreateVersion7(), """{"v":1}""", Now);

    private static Notification Started()
    {
        Notification notification = Pending();
        notification.StartSend(1, "en", Now).ShouldBeTrue();
        return notification;
    }

    /// <summary>Each move from <c>Pending</c>, with the state it leaves and the reason it stamps.</summary>
    public static TheoryData<string, Func<Notification, bool>, NotificationStatus, string?> Moves => new()
    {
        { "a decline the customer cancelled", n => n.Suppress(Later), NotificationStatus.Suppressed, null },
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
        { "the send is about to start", n => n.StartSend(2, "en,kk", Later), NotificationStatus.Pending, null },
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

        notification.StartSend(2, "en,kk", Later).ShouldBeTrue();

        notification.TemplateVersion.ShouldBe(2);
        notification.Languages.ShouldBe("en,kk");
        notification.SendStartedAt.ShouldBe(Later);
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

        notification.StartSend(3, "ru", Later).ShouldBeFalse();

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
        // ADR-052's outcomes, the relay's two refusals and §11.7's erasure, and nothing a caller could misspell.
        NotificationReasons.All.ShouldBe(
            ["no_such_customer", "recipient_refused", "not_a_mailbox", "gave_up", "erased"],
            ignoreOrder: true);
    }

    [Fact]
    public void An_empty_event_or_order_id_is_refused_at_the_door()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Notification.Pending(Guid.Empty, "order-placed", Guid.CreateVersion7(), """{"v":1}""", Now));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Notification.Pending(Guid.CreateVersion7(), "order-placed", Guid.Empty, """{"v":1}""", Now));
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
        Should.Throw<ArgumentException>(() =>
            Notification.Pending(Guid.CreateVersion7(), " ", Guid.CreateVersion7(), """{"v":1}""", Now));
        Should.Throw<ArgumentException>(() =>
            Notification.Pending(Guid.CreateVersion7(), "order-placed", Guid.CreateVersion7(), "", Now));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Notification.Pending(
                Guid.CreateVersion7(),
                new string('k', NotificationLimits.MaxTemplateKeyLength + 1),
                Guid.CreateVersion7(),
                """{"v":1}""",
                Now));
        Should.Throw<ArgumentOutOfRangeException>(() => Pending().StartSend(0, "en", Later));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Pending().StartSend(1, new string('x', NotificationLimits.MaxLanguagesLength + 1), Later));
    }

    private static IEnumerable<Notification> Terminals()
    {
        Notification suppressed = Pending();
        suppressed.Suppress(Now);

        Notification undeliverable = Pending();
        undeliverable.MarkUndeliverable(NotificationReasons.GaveUp, Now);

        Notification sent = Started();
        sent.MarkSent(Now);

        return [suppressed, undeliverable, sent];
    }
}
