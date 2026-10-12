using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-098: a send pass never keeps or mails an erased customer's mailbox, and once the erasure is visible
/// never fetches it.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ErasedContactTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetWithRelayAsync();
        await fixture.ExecuteAsync("DELETE FROM notifications.PersonalDataErasures;");
    }

    public async ValueTask DisposeAsync() =>
        await fixture.ExecuteAsync("DELETE FROM notifications.PersonalDataErasures;");

    [Fact]
    public async Task A_notice_claimed_after_the_erasure_asks_the_owner_for_nothing_and_sends_nothing()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromDays(1));
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.OrderAsync(order, customer);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
        await ErasedSubjectIntakeTests.RecordErasureAsync(fixture, customer, At);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        fixture.ContactCalls(customer).ShouldBe(0, "the owner is not asked for an erased customer's mailbox");
        (await fixture.ContactAsync(customer)).ShouldBeNull("the stale row the erasure's delete missed goes too");
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Undeliverable);
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_erasure_landing_while_the_owner_answers_leaves_no_contact_row_and_no_mail()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en", delay: TimeSpan.FromMilliseconds(900));
        await fixture.OrderAsync(order, customer);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);

        Task<SendPass> pass = fixture.RunSendPassAsync();

        // The row is leased, then the pre-check passes and the owner stalls: the erasure lands in the window ADR-098
        // names. An erasure recorded earlier is caught by the pre-check, so only a late one can fail the case.
        await ServiceFixture.WaitUntilAsync(async () =>
            await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
                "AND Status = 'Pending' AND LockedUntil > SYSDATETIMEOFFSET()",
                owed.NotificationId) == 1);
        await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        await ErasedSubjectIntakeTests.RecordErasureAsync(fixture, customer, At);

        (await pass).ShouldBe(new SendPass(1, 1));

        (await fixture.ContactAsync(customer)).ShouldBeNull("the save is followed by a check that deletes it");
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Undeliverable);
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_erasure_landing_while_a_stale_contact_waits_out_a_failing_owner_is_not_served()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, 503, delay: TimeSpan.FromMilliseconds(150));
        await fixture.OrderAsync(order, customer);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);

        Task<SendPass> pass = fixture.RunSendPassAsync();

        // Leased, then the pre-check passes and the owner fails through its retries: the stale row would be served.
        await ServiceFixture.WaitUntilAsync(async () =>
            await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
                "AND Status = 'Pending' AND LockedUntil > SYSDATETIMEOFFSET()",
                owed.NotificationId) == 1);
        await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        await ErasedSubjectIntakeTests.RecordErasureAsync(fixture, customer, At);

        (await pass).ShouldBe(new SendPass(1, 1));

        (await fixture.ContactAsync(customer)).ShouldBeNull();
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Undeliverable);
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty("a stale mailbox is not served for an erased customer");
    }
}
