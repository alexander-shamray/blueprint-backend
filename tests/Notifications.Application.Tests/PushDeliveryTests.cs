using Notifications.Application.Delivery;
using Notifications.Application.Devices;
using Notifications.Application.Preferences;
using Notifications.Application.Records;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>A delivery's moves, which are a notice's, and the device rule its send reads (the push design).</summary>
public class PushDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);
    private static readonly Guid Customer = Guid.CreateVersion7();
    private static readonly Guid Installation = Guid.CreateVersion7();
    private static readonly Guid Entry = Guid.CreateVersion7();

    private static Notification Notice()
    {
        Notification notice = Notification.Pending(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "order-placed",
            ConsentVocabulary.Push,
            Guid.CreateVersion7(),
            """{"v":1}""",
            Now);
        notice.AssignCustomer(Customer).ShouldBeTrue();
        return notice;
    }

    private static PushDelivery Delivery(Guid? entry = null) =>
        PushDelivery.For(Notice(), Installation, "fcm", entry, "kk,en", Now);

    /// <summary>Each terminal move from <c>Pending</c>, with the reason it stamps.</summary>
    public static TheoryData<string, Func<PushDelivery, bool>, NotificationStatus, string?> Moves => new()
    {
        {
            "the customer's newest entry withdraws push since the fan-out",
            d => d.OptOut(Entry, Later),
            NotificationStatus.Suppressed,
            NotificationReasons.OptedOut
        },
        {
            "the provider says the token is valid for no app",
            d => d.MarkUndeliverable(NotificationReasons.Unregistered, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.Unregistered
        },
        {
            "the provider will not deliver this token for this sender",
            d => d.MarkUndeliverable(NotificationReasons.InvalidToken, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.InvalidToken
        },
        {
            "the device left the customer",
            d => d.MarkUndeliverable(NotificationReasons.NoDevice, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.NoDevice
        },
        {
            "the notice's give-up age passes",
            d => d.MarkUndeliverable(NotificationReasons.GaveUp, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.GaveUp
        },
    };

    [Fact]
    public void A_fan_out_writes_a_pending_delivery_carrying_the_notice_s_customer_and_correlation()
    {
        Notification notice = Notice();

        PushDelivery delivery = PushDelivery.For(notice, Installation, "apns", Entry, "en", Now);

        delivery.Status.ShouldBe(NotificationStatus.Pending);
        delivery.NotificationId.ShouldBe(notice.NotificationId);
        delivery.CustomerId.ShouldBe(Customer, "an erasure finds the row by it, without its notice");
        delivery.CorrelationId.ShouldBe(notice.CorrelationId);
        delivery.ConsentEntryId.ShouldBe(Entry);
        delivery.Languages.ShouldBe("en");
        delivery.NextAttemptAt.ShouldBe(Now, "a new row is due at once");
        delivery.SendStartedAt.ShouldBeNull();
    }

    [Fact]
    public void A_notice_whose_customer_is_unknown_cannot_fan_out()
    {
        Notification notice = Notification.Pending(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "order-placed",
            ConsentVocabulary.Push,
            Guid.CreateVersion7(),
            """{"v":1}""",
            Now);

        Should.Throw<InvalidOperationException>(() => PushDelivery.For(notice, Installation, "fcm", null, "en", Now));
    }

    [Fact]
    public void A_platform_outside_the_pair_is_the_caller_s_defect()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            PushDelivery.For(Notice(), Installation, "sms", null, "en", Now));
    }

    [Theory]
    [MemberData(nameof(Moves))]
    public void Each_terminal_move_lands_where_the_record_says_and_ends_the_row(
        string because,
        Func<PushDelivery, bool> move,
        NotificationStatus status,
        string? reason)
    {
        PushDelivery delivery = Delivery();

        move(delivery).ShouldBeTrue(because);

        delivery.Status.ShouldBe(status, because);
        delivery.Reason.ShouldBe(reason, because);
        delivery.CompletedAt.ShouldBe(Later, because);
        move(delivery).ShouldBeFalse("an ended row has outgrown every move");
    }

    [Fact]
    public void A_notice_s_reason_is_refused_on_a_delivery()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => Delivery().MarkUndeliverable(NotificationReasons.RecipientRefused, Later),
            "a relay's answer never lands on a push");
    }

    [Fact]
    public void The_intent_stamps_its_version_and_newest_entry_once_and_a_send_needs_it()
    {
        PushDelivery delivery = Delivery(entry: null);

        delivery.MarkSent(Later).ShouldBeFalse("only a row whose intent was committed can be sent");
        delivery.StartSend(2, Entry, Later).ShouldBeTrue();
        delivery.StartSend(3, null, Later).ShouldBeFalse("a resend is sent under the first intent, not restamped");

        delivery.TemplateVersion.ShouldBe(2);
        delivery.ConsentEntryId.ShouldBe(Entry, "a newer entry that still opts in is the one stamped (ADR-120)");
        delivery.OptOut(null, Later).ShouldBeFalse("a send under way is decided");
        delivery.MarkSent(Later).ShouldBeTrue();
        delivery.Status.ShouldBe(NotificationStatus.Sent);
    }

    public static TheoryData<string, PushDevice?, bool> Devices => new()
    {
        { "the row holds the customer's token on the platform", Device(Customer, "fcm"), true },
        { "the row is gone", null, false },
        { "the row was cleared", new PushDevice(Installation, null, null, null, null), false },
        { "the row moved to another subject", Device(Guid.CreateVersion7(), "fcm"), false },
        { "the row now holds another platform's token", Device(Customer, "apns"), false },
    };

    [Theory]
    [MemberData(nameof(Devices))]
    public void A_delivery_s_device_reaches_its_customer_only_while_it_still_holds_their_token(
        string because,
        PushDevice? device,
        bool reaches)
    {
        SendRules.Reaches(device, Customer, "fcm").ShouldBe(reaches, because);
    }

    [Fact]
    public void A_device_prints_its_installation_and_never_its_token()
    {
        PushDevice device = Device(Customer, "fcm");

        device.ToString().ShouldNotContain(device.Token!);
        device.ToString().ShouldContain(Installation.ToString());
    }

    private static PushDevice Device(Guid subject, string platform) =>
        new(Installation, subject, platform, "com.example.shop", "token-" + subject.ToString("N"));
}
