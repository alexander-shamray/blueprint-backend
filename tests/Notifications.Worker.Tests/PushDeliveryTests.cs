using System.Text;
using System.Text.Json.Nodes;
using Common.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Devices;
using Notifications.Application.Devices.RegisterDevice;
using Notifications.Application.Preferences;
using Notifications.Application.Preferences.SetPreference;
using Notifications.Application.Privacy.ErasePersonalData;
using Notifications.Application.Push;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.Push;
using Notifications.TestSupport;
using Shouldly;
using WireMock;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The push design's section 10 against SQL Server and the simulator: the fan-out and the delivery.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PushDeliveryTests(ServiceFixture fixture, PushSimulator simulator)
    : IClassFixture<PushSimulator>, IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private NotificationsWorkerFactory _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetWithRelayAsync();
        simulator.Reset();
        _host = fixture.NewWorkerHost(settings: PushSettings.Both(simulator.BaseUrl));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_push_notice_fans_out_to_one_delivery_per_device_and_a_second_pass_adds_none()
    {
        (Guid customer, Notification notice) = await OwedAsync("kk");
        Guid phone = await RegisterAsync(customer, FcmPushProvider.Name, "device-phone");
        Guid tablet = await RegisterAsync(customer, ApnsPushProvider.Name, "device-tablet");

        (await FanOutAsync()).ShouldBe(new SendPass(1, 1));
        await fixture.ClearBackoffAsync(notice.NotificationId);
        (await FanOutAsync()).ShouldBe(new SendPass(0, 0), "a dispatched notice is never claimed again");

        Notification dispatched = await fixture.NotificationAsync(notice.NotificationId);
        dispatched.Status.ShouldBe(NotificationStatus.Dispatched);
        dispatched.Languages.ShouldBe("kk", "the customer's locale chose the language");

        List<PushDelivery> deliveries = await DeliveriesAsync(notice.NotificationId);
        deliveries.Select(d => (d.InstallationId, d.Platform)).ShouldBe(
            [(phone, FcmPushProvider.Name), (tablet, ApnsPushProvider.Name)],
            ignoreOrder: true);
        deliveries.ShouldAllBe(d =>
            d.Status == NotificationStatus.Pending && d.CustomerId == customer && d.Languages == "kk");
        simulator.Server.LogEntries.ShouldBeEmpty("the fan-out calls no provider");
    }

    [Fact]
    public async Task The_key_refuses_a_second_delivery_of_one_notice_to_one_installation()
    {
        (Guid customer, Notification notice) = await OwedAsync("en");
        Guid phone = await RegisterAsync(customer, FcmPushProvider.Name, "device-phone");
        await FanOutAsync();

        await Should.ThrowAsync<Microsoft.Data.SqlClient.SqlException>(() => fixture.ExecuteAsync(
            "INSERT notifications.PushDeliveries (DeliveryId, NotificationId, InstallationId, Platform, Status, " +
            "CreatedAt, Attempts, NextAttemptAt, CorrelationId) VALUES (NEWID(), {0}, {1}, 'fcm', 'Pending', " +
            "SYSDATETIMEOFFSET(), 0, SYSDATETIMEOFFSET(), NEWID());",
            notice.NotificationId,
            phone));
    }

    [Fact]
    public async Task A_customer_with_no_device_ends_the_notice_no_device_and_asks_nobody_for_a_contact()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        await fixture.OrderAsync(order, customer);
        Notification notice =
            await fixture.PendingAsync(TemplateKeys.OrderPlaced, order, channel: ConsentVocabulary.Push);

        await FanOutAsync();

        Notification ended = await fixture.NotificationAsync(notice.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.NoDevice);
        fixture.ContactCalls(customer).ShouldBe(0, "the devices are read before the language is");
    }

    [Fact]
    public async Task A_withdrawn_push_pair_suppresses_the_notice_under_its_entry_and_writes_no_delivery()
    {
        (Guid customer, Notification notice) = await OwedAsync("en");
        await RegisterAsync(customer, FcmPushProvider.Name, "device-phone");
        Guid withdrawal = await SetPushAsync(customer, optedIn: false);

        await FanOutAsync();

        Notification ended = await fixture.NotificationAsync(notice.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Suppressed);
        ended.Reason.ShouldBe(NotificationReasons.OptedOut);
        ended.ConsentEntryId.ShouldBe(withdrawal);
        (await DeliveriesAsync(notice.NotificationId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_delivery_is_sent_under_its_intent_with_the_event_as_its_collapse_key()
    {
        (Guid customer, Notification notice) = await OwedAsync("en");
        await RegisterAsync(customer, FcmPushProvider.Name, "device-phone");
        await FanOutAsync();

        (await PushPassAsync()).ShouldBe(new SendPass(1, 1));

        PushDelivery sent = (await DeliveriesAsync(notice.NotificationId)).ShouldHaveSingleItem();
        sent.Status.ShouldBe(NotificationStatus.Sent);
        sent.SendStartedAt.ShouldNotBeNull("the intent precedes the call");
        sent.TemplateVersion.ShouldBe(1);
        string body = FcmSends().ShouldHaveSingleItem().Body!;
        body.ShouldContain(notice.EventId.ToString("N"), Case.Sensitive, "a resend replaces rather than stacks");
        body.ShouldContain(notice.OrderId.ToString("D"));
    }

    [Fact]
    public async Task A_device_moved_to_another_subject_since_the_fan_out_ends_the_delivery_and_calls_nothing()
    {
        (Guid customer, Notification notice) = await OwedAsync("en");
        Guid phone = await RegisterAsync(customer, FcmPushProvider.Name, "device-phone");
        await FanOutAsync();
        await RegisterAsync(Guid.CreateVersion7(), FcmPushProvider.Name, "device-phone", phone, sequence: 2);

        (await PushPassAsync()).ShouldBe(new SendPass(1, 1));

        PushDelivery ended = (await DeliveriesAsync(notice.NotificationId)).ShouldHaveSingleItem();
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.NoDevice, "ADR-117: a moved device never shows the subject before");
        FcmSends().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(FcmPushProvider.Name, "unregistered-1", "unregistered", false)]
    [InlineData(ApnsPushProvider.Name, "unregistered-1", "unregistered", false)]
    [InlineData(FcmPushProvider.Name, "invalid-1", "invalid_token", true)]
    [InlineData(ApnsPushProvider.Name, "invalid-1", "invalid_token", true)]
    public async Task A_refusal_ends_the_delivery_and_only_an_unregistered_one_deletes_the_device(
        string platform,
        string token,
        string reason,
        bool kept)
    {
        (Guid customer, Notification notice) = await OwedAsync("en");
        Guid installation = await RegisterAsync(customer, platform, token);
        await FanOutAsync();

        await PushPassAsync();

        PushDelivery ended = (await DeliveriesAsync(notice.NotificationId)).ShouldHaveSingleItem();
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(reason);
        (await DeviceAsync(installation) is not null).ShouldBe(kept, "ADR-121 keeps the row on an invalid token");
    }

    [Fact]
    public async Task A_token_rotated_under_a_send_keeps_its_row_when_the_old_token_is_answered_unregistered()
    {
        (Guid customer, Notification notice) = await OwedAsync("en");
        Guid installation = await RegisterAsync(customer, FcmPushProvider.Name, "slow-unregistered-1");
        await FanOutAsync();

        // The simulator holds the answer for a second, and logs a request only once answered, so the intent is the
        // sign the call is under way; the token was read before it, and the app registers its new one meanwhile.
        Task<SendPass> pass = PushPassAsync();
        await ServiceFixture.WaitUntilAsync(async () =>
            await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM notifications.PushDeliveries " +
                "WHERE NotificationId = {0} AND SendStartedAt IS NOT NULL",
                notice.NotificationId) == 1);
        await RegisterAsync(customer, FcmPushProvider.Name, "device-rotated", installation, sequence: 2);
        FcmSends().ShouldBeEmpty("the registration must land while the provider still holds its answer");
        await pass;

        PushDelivery ended = (await DeliveriesAsync(notice.NotificationId)).ShouldHaveSingleItem();
        ended.Reason.ShouldBe(NotificationReasons.Unregistered);
        PushDevice kept = (await DeviceAsync(installation)).ShouldNotBeNull("the row no longer held the token sent");
        kept.Token.ShouldBe("device-rotated");
    }

    [Fact]
    public async Task An_open_relay_parks_the_mail_notices_and_still_claims_the_push_ones()
    {
        (_, Notification push) = await OwedAsync("en");
        Notification mail = await fixture.PendingAsync(TemplateKeys.OrderPlaced, push.OrderId);

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        SendClaims claims = scope.ServiceProvider.GetRequiredService<SendClaims>();

        SendWork claimed = (await claims.ClaimAsync(mailParked: true, Ct)).ShouldHaveSingleItem();
        claimed.NotificationId.ShouldBe(push.NotificationId);
        claimed.Channel.ShouldBe(ConsentVocabulary.Push);
        (await claims.ClaimAsync(mailParked: false, Ct)).ShouldHaveSingleItem().NotificationId
            .ShouldBe(mail.NotificationId, "the push notice is leased; the mail notice waits for the relay");
    }

    [Fact]
    public async Task One_provider_s_open_breaker_parks_its_platform_s_deliveries_while_the_other_sends()
    {
        (Guid customer, Notification notice) = await OwedAsync("en");
        await RegisterAsync(customer, FcmPushProvider.Name, "device-phone");
        await RegisterAsync(customer, ApnsPushProvider.Name, "device-tablet");
        await FanOutAsync();
        await OpenBreakerAsync(ApnsPushProvider.Name);

        (await PushPassAsync()).ShouldBe(new SendPass(1, 1), "only FCM's delivery is claimed");

        List<PushDelivery> deliveries = await DeliveriesAsync(notice.NotificationId);
        deliveries.Single(d => d.Platform == FcmPushProvider.Name).Status.ShouldBe(NotificationStatus.Sent);
        PushDelivery parked = deliveries.Single(d => d.Platform == ApnsPushProvider.Name);
        parked.Status.ShouldBe(NotificationStatus.Pending);
        parked.Attempts.ShouldBe(0, "a parked row is never claimed, so it keeps its backoff");
    }

    [Fact]
    public async Task An_erasure_deletes_the_pending_deliveries_anonymises_the_ended_and_clears_the_device()
    {
        (Guid customer, Notification notice) = await OwedAsync("en");
        Guid installation = await RegisterAsync(customer, FcmPushProvider.Name, "device-phone");
        await RegisterAsync(customer, ApnsPushProvider.Name, "device-tablet");
        await FanOutAsync();
        await OpenBreakerAsync(ApnsPushProvider.Name);
        await PushPassAsync();

        Result<int> erased;
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            erased = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(
                new ErasePersonalDataCommand(Guid.CreateVersion7(), customer),
                Ct);
        }

        List<PushDelivery> left = await DeliveriesAsync(notice.NotificationId);
        PushDelivery sent = left.ShouldHaveSingleItem("the pending APNs delivery is deleted");
        sent.Status.ShouldBe(NotificationStatus.Sent, "the evidence of a send stays");
        sent.CustomerId.ShouldBeNull();
        sent.Languages.ShouldBeNull();
        (await DeviceAsync(installation)).ShouldNotBeNull().SubjectId.ShouldBeNull("ADR-117 clears, keeping the id");
        // One per statement that touched a row: the ended notice anonymised, both deliveries (one deleted, one
        // anonymised), the order record, the contact and both devices. No waiting notice and no consent entry exist.
        erased.Value.ShouldBe(1 + 2 + 1 + 1 + 2, "each statement's rows are counted once, and none is lost");
    }

    [Fact]
    public async Task Retention_ages_a_dispatched_notice_from_its_last_delivery_s_end_and_never_while_one_is_pending()
    {
        (Guid customer, Notification notice) = await OwedAsync("en");
        await RegisterAsync(customer, ApnsPushProvider.Name, "device-tablet");
        await FanOutAsync();
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET CompletedAt = DATEADD(day, -2000, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            notice.NotificationId);

        (await fixture.PurgeNotificationsRetentionAsync()).Notifications.ShouldBe(0);
        (await DeliveriesAsync(notice.NotificationId)).ShouldHaveSingleItem();

        await PushPassAsync();
        (await fixture.PurgeNotificationsRetentionAsync()).Notifications.ShouldBe(
            0,
            "ADR-053 rule 4's window runs from the send's own end, not from the fan-out");

        await fixture.ExecuteAsync(
            "UPDATE notifications.PushDeliveries SET CompletedAt = DATEADD(day, -1999, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            notice.NotificationId);
        (await fixture.PurgeNotificationsRetentionAsync()).Notifications.ShouldBe(1, "its last delivery is past it");
        (await DeliveriesAsync(notice.NotificationId)).ShouldBeEmpty("the ended delivery ages with its notice");
    }

    [Fact]
    public async Task The_longest_push_in_every_language_reaches_the_provider_under_its_payload_ceiling()
    {
        // No usable locale, so the whole set in one push, Kazakh script first: the worst case for the 4 KB ceiling.
        (Guid customer, Notification notice) = await OwedAsync(locale: null, TemplateKeys.OrderCancelled);
        await RegisterAsync(customer, FcmPushProvider.Name, "device-phone");
        await FanOutAsync();

        await PushPassAsync();

        (await DeliveriesAsync(notice.NotificationId)).ShouldHaveSingleItem().Languages.ShouldBe("kk,en");
        // Measured as sent, the JSON's escaping of every non-ASCII character included.
        string body = FcmSends().ShouldHaveSingleItem().Body!;
        Encoding.UTF8.GetByteCount(body).ShouldBeLessThan(4096);
        JsonNode.Parse(body)!["message"]!["notification"]!["title"]!.GetValue<string>()
            .ShouldStartWith("Тапсырысыңыз жойылды / ");
    }

    private async Task<(Guid Customer, Notification Notice)> OwedAsync(
        string? locale,
        string templateKey = TemplateKeys.OrderPlaced)
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        await fixture.OrderAsync(order, customer);
        await fixture.StageContactAsync(customer, Mailbox, locale, TimeSpan.Zero);

        return (customer, await fixture.PendingAsync(templateKey, order, channel: ConsentVocabulary.Push));
    }

    private async Task<Guid> RegisterAsync(
        Guid subject,
        string platform,
        string token,
        Guid? installation = null,
        long sequence = 1)
    {
        Guid id = installation ?? Guid.CreateVersion7();
        string app = platform == FcmPushProvider.Name ? PushSettings.FcmApp : PushSettings.ApnsApp;

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        Result<DeviceWrite> written = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(
            new RegisterDeviceCommand(subject, id, platform, app, token, sequence),
            Ct);
        written.Value.Outcome.ShouldBe(DeviceWriteOutcome.Applied);

        return id;
    }

    private async Task<Guid> SetPushAsync(Guid subject, bool optedIn)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();

        Result<PreferenceWrite> written = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(
            new SetPreferenceCommand(subject, TemplateKeys.OrderPlaced, ConsentVocabulary.Push, optedIn, null, "app"),
            Ct);

        return written.Value.State.Version!.Value;
    }

    // A delivery is stamped due on the host's clock and claimed on the engine's, which can trail it by a moment, as
    // ServiceFixture.WaitUntilDueAsync says of a notice; so a push pass made at once could find nothing due.
    private async Task<SendPass> FanOutAsync()
    {
        SendPass pass = await _host.Services.GetRequiredService<SendWorker>().RunOnceAsync(Ct);

        await ServiceFixture.WaitUntilAsync(async () =>
            await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM notifications.PushDeliveries " +
                "WHERE Status = 'Pending' AND NextAttemptAt > SYSDATETIMEOFFSET()") == 0);

        return pass;
    }

    private Task<SendPass> PushPassAsync() => _host.Services.GetRequiredService<PushSendWorker>().RunOnceAsync(Ct);

    // Each send is two faulted attempts, so half the minimum throughput, rounded up, fills a window past it.
    private async Task OpenBreakerAsync(string platform)
    {
        IPushChannel channel = _host.Services.GetRequiredService<IPushChannel>();
        IPushProvider provider = _host.Services.GetServices<IPushProvider>().Single(p => p.Platform == platform);

        for (int i = 0; i < (ApnsHop.CircuitBreakerMinimumThroughput + 1) / 2; i++)
        {
            await Should.ThrowAsync<PushUnavailableException>(() =>
                channel.SendAsync(PushSettings.To(platform, $"unavailable-{i}"), Ct));
        }

        provider.Pipeline.IsOpen.ShouldBeTrue();
        simulator.Server.ResetLogEntries();
    }

    private async Task<List<PushDelivery>> DeliveriesAsync(Guid notificationId)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().PushDeliveries
            .AsNoTracking()
            .Where(d => d.NotificationId == notificationId)
            .ToListAsync(Ct);
    }

    private async Task<PushDevice?> DeviceAsync(Guid installation)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<PushDevices>().GetAsync(installation, Ct);
    }

    private IRequestMessage[] FcmSends() =>
    [
        .. simulator.Server.LogEntries
            .Select(e => e.RequestMessage!)
            .Where(r => r.Path.EndsWith("/messages:send", StringComparison.Ordinal))
    ];
}
