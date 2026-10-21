using Common.Domain;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-100: a mailbox the owner returns after the customer's erasure is not written back to the
/// table.</summary>
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
    public async Task An_erasure_landing_while_the_owner_answers_leaves_no_contact_row()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en", delay: TimeSpan.FromMilliseconds(900));
        await fixture.OrderAsync(order, customer);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);

        Task<SendPass> pass = fixture.RunSendPassAsync();

        // The row is leased and the owner stalls: the erasure lands in the window ADR-097 names, before the save.
        await ServiceFixture.WaitUntilAsync(async () =>
            await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
                "AND Status = 'Pending' AND LockedUntil > SYSDATETIMEOFFSET()",
                owed.NotificationId) == 1);
        await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        await ErasedSubjectIntakeTests.RecordErasureAsync(fixture, customer, At);

        (await pass).ShouldBe(new SendPass(1, 1));

        (await fixture.ContactAsync(customer)).ShouldBeNull("the save is followed by a check that deletes it");
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(
            NotificationStatus.Sent,
            "the pass still sends the answer it holds (ADR-100)");
    }

    [Fact]
    public async Task A_customer_already_erased_is_not_asked_of_the_owner_and_nothing_is_sent()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.OrderAsync(order, customer);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
        await ErasedSubjectIntakeTests.RecordErasureAsync(fixture, customer, At);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));

        fixture.ContactCalls(customer).ShouldBe(0, "the question comes before the owner call (ADR-101)");
        (await fixture.ContactAsync(customer)).ShouldBeNull();
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Pending);
    }

    [Fact]
    public async Task A_request_closed_before_the_records_earliest_event_is_not_asked_by_the_send_pass()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.OrderAsync(order, customer, sourcedAt: At);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);

        // Closed, so the owner had anonymised the subject before the record's event carried them (ADR-139).
        await ErasedSubjectIntakeTests.RecordErasureAsync(
            fixture,
            customer,
            At.AddDays(-9),
            closedAt: At - PersonalDataErasure.ClockMargin - TimeSpan.FromHours(1));

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        fixture.ContactCalls(customer).ShouldBe(1);
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task A_request_closed_inside_the_margin_before_the_records_earliest_event_is_asked()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.OrderAsync(order, customer, sourcedAt: At);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);

        await ErasedSubjectIntakeTests.RecordErasureAsync(
            fixture,
            customer,
            At.AddDays(-9),
            closedAt: At - PersonalDataErasure.ClockMargin + TimeSpan.FromHours(1));

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));

        fixture.ContactCalls(customer).ShouldBe(0);
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Pending);
    }

    [Fact]
    public async Task A_record_from_before_the_column_asks_every_row_so_a_request_closed_long_ago_stops_the_send()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.OrderAsync(order, customer, sourcedAt: null);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);

        await ErasedSubjectIntakeTests.RecordErasureAsync(
            fixture,
            customer,
            At.AddDays(-90),
            closedAt: At.AddDays(-60));

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));

        fixture.ContactCalls(customer).ShouldBe(0);
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Pending);
    }

    [Fact]
    public async Task The_contact_read_asks_every_row_so_a_request_closed_long_ago_deletes_the_saved_contact()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en", delay: TimeSpan.FromMilliseconds(900));
        await fixture.OrderAsync(order, customer, sourcedAt: At);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);

        Task<SendPass> pass = fixture.RunSendPassAsync();
        await LandDuringTheOwnersAnswerAsync(owed);

        // Closed well before the record's SourcedAt, so the send pass's own check lets the row through (ADR-139).
        await ErasedSubjectIntakeTests.RecordErasureAsync(
            fixture,
            customer,
            At.AddDays(-9),
            closedAt: At - PersonalDataErasure.ClockMargin - TimeSpan.FromHours(1));

        (await pass).ShouldBe(new SendPass(1, 1));

        (await fixture.ContactAsync(customer)).ShouldBeNull("the contact read asks every row, not one order's");
    }

    // The row is leased and the owner stalls: a request lands in the window ADR-097 names, before the save.
    private async Task LandDuringTheOwnersAnswerAsync(Notification owed)
    {
        await ServiceFixture.WaitUntilAsync(async () =>
            await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
                "AND Status = 'Pending' AND LockedUntil > SYSDATETIMEOFFSET()",
                owed.NotificationId) == 1);
        await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
    }

    [Fact]
    public async Task An_order_placed_under_the_request_is_still_sent()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en");
        Guid request = await ErasedSubjectIntakeTests.RecordErasureAsync(fixture, customer, At);
        await fixture.OrderAsync(order, customer, erasedUnder: [request]);
        Notification owed = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        fixture.ContactCalls(customer).ShouldBe(1);
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(
            NotificationStatus.Sent,
            "the request names an earlier erasure, which ADR-099 does not count against this order");
    }
}
