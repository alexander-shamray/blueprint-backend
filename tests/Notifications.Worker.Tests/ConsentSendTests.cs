using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Preferences;
using Notifications.Application.Preferences.SetPreference;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-120's step in the mail send: decided when the send is made, under the entry it names.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ConsentSendTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Guid> SetAsync(Guid subject, bool optedIn)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        Result<PreferenceWrite> written = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(
            new SetPreferenceCommand(
                subject,
                TemplateKeys.OrderConfirmed,
                ConsentVocabulary.Email,
                optedIn,
                null,
                "commerce-app"),
            Ct);

        return written.Value.State.Version!.Value;
    }

    private async Task<Notification> OwedAsync(Guid order, Guid customer)
    {
        await fixture.OrderAsync(order, customer);

        return await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
    }

    [Fact]
    public async Task A_withdrawn_pair_suppresses_the_notice_under_its_entry_and_asks_nobody_for_a_mailbox()
    {
        (Guid order, Guid customer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        fixture.ContactAnswers(customer, Mailbox, "en");
        Guid withdrawal = await SetAsync(customer, optedIn: false);
        Notification owed = await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification ended = await fixture.NotificationAsync(owed.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Suppressed);
        ended.Reason.ShouldBe(NotificationReasons.OptedOut);
        ended.ConsentEntryId.ShouldBe(withdrawal, "the record shows the withdrawal it rested on");
        fixture.ContactCalls(customer).ShouldBe(0, "decided before the contact read (ADR-120)");
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_opted_in_pair_is_sent_and_the_record_names_the_entry_it_was_decided_under()
    {
        (Guid order, Guid customer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        fixture.ContactAnswers(customer, Mailbox, "en");
        Guid optIn = await SetAsync(customer, optedIn: true);
        Notification owed = await OwedAsync(order, customer);

        await fixture.RunSendPassAsync();

        Notification sent = await fixture.NotificationAsync(owed.NotificationId);
        sent.Status.ShouldBe(NotificationStatus.Sent);
        sent.ConsentEntryId.ShouldBe(optIn);
    }

    [Fact]
    public async Task A_pair_with_no_entry_is_sent_on_its_service_default_and_names_no_entry()
    {
        (Guid order, Guid customer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        fixture.ContactAnswers(customer, Mailbox, "en");
        Notification owed = await OwedAsync(order, customer);

        await fixture.RunSendPassAsync();

        Notification sent = await fixture.NotificationAsync(owed.NotificationId);
        sent.Status.ShouldBe(NotificationStatus.Sent);
        sent.ConsentEntryId.ShouldBeNull("a default decided it");
    }

    [Fact]
    public async Task A_required_pair_is_sent_whatever_the_journal_holds_and_names_no_entry()
    {
        (Guid order, Guid customer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        fixture.ContactAnswers(customer, Mailbox, "en");
        await SetAsync(customer, optedIn: false);
        Notification owed = await OwedAsync(order, customer);
        using NotificationsWorkerFactory host =
            fixture.NewWorkerHost(required: [$"{TemplateKeys.OrderConfirmed}/{ConsentVocabulary.Email}"]);

        await host.Services.GetRequiredService<SendWorker>().RunOnceAsync(Ct);

        Notification sent = await fixture.NotificationAsync(owed.NotificationId);
        sent.Status.ShouldBe(NotificationStatus.Sent, "a duty is not the subject's to switch off");
        sent.ConsentEntryId.ShouldBeNull("a requirement decided it");
    }

    [Fact]
    public async Task A_withdrawal_after_the_intent_does_not_stop_the_resend_it_already_owes()
    {
        // ADR-120's residual: the pass reads the entry before it calls the channel, so a send already made stands.
        (Guid order, Guid customer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        fixture.ContactAnswers(customer, Mailbox, "en");
        Notification owed = await OwedAsync(order, customer);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET SendStartedAt = SYSDATETIMEOFFSET(), TemplateVersion = 1, " +
            "Languages = 'en' WHERE NotificationId = {0};",
            owed.NotificationId);
        await SetAsync(customer, optedIn: false);

        await fixture.RunSendPassAsync();

        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Sent);
        (await fixture.Relay.SingleAsync(Ct)).ShouldNotBeNull();
    }
}
