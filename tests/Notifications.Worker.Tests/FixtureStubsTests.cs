using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Application.Mail;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Shouldly;
using Xunit;
using MessagingRegistration = Notifications.Infrastructure.Messaging.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>The fixture's stand-ins, read through the host's own adapters, so a later stub is a real one.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class FixtureStubsTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_second_host_takes_no_delivery_from_the_fixture_s_queues()
    {
        // A consumer it held would take every other delivery out of the fixture's sight; its bus connects late, so
        // the count is watched for a while rather than read once.
        string[] queues = [MessagingRegistration.EventsQueue, MessagingRegistration.PrivacyQueue];
        await ServiceFixture.WaitUntilAsync(async () => await ConsumersAsync(queues) == 2);
        using NotificationsWorkerFactory second = fixture.NewWorkerHost();
        _ = second.Services.GetRequiredService<SendWorker>();

        DateTimeOffset until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < until)
        {
            (await ConsumersAsync(queues)).ShouldBe(2, "one consumer a queue, the fixture's own");
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }
    }

    [Fact]
    public async Task The_stub_keycloak_answers_the_host_s_own_adapter_as_the_admin_api_does()
    {
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, "aigerim@example.test", "kk-KZ");

        (await ReadAsync(customer)).ShouldBe(new ContactLookup.Found("aigerim@example.test", "kk-KZ"));
        fixture.ContactCalls(customer).ShouldBe(1);
    }

    [Fact]
    public async Task A_user_the_stub_does_not_know_is_no_such_customer()
    {
        // The admin API's answer for no such user is a 404 carrying its error body, which the adapter reads.
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, 404);

        (await ReadAsync(customer)).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task The_host_s_relay_is_the_fixture_s()
    {
        MailResult result = await fixture.Factory.Services.GetRequiredService<IMailChannel>().SendAsync(
            new OutboundMail(
                "aigerim@example.test",
                "Your order is placed",
                "Order 42 is placed.",
                new MailMessageId(Guid.CreateVersion7(), TemplateKeys.OrderPlaced),
                Guid.CreateVersion7(),
                ["en"]),
            Ct);

        result.ShouldBe(new MailResult.Accepted());
        (await fixture.Relay.WaitForAsync(1, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_staged_notice_is_pending_due_and_unleased()
    {
        Notification staged = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
            "AND Status = 'Pending' AND NextAttemptAt <= SYSDATETIMEOFFSET() AND LockedUntil IS NULL",
            staged.NotificationId)).ShouldBe(1);
    }

    private async Task<int> ConsumersAsync(string[] queues) =>
        (await Task.WhenAll(queues.Select(fixture.ConsumersAsync))).Sum();

    private async Task<ContactLookup> ReadAsync(Guid customer)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactSource>().GetAsync(customer, Ct);
    }
}
