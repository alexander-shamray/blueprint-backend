using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Contacts;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;
using MessagingRegistration = Notifications.Infrastructure.Messaging.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>A dependency that dies, each on its own host, as the breakers these cases fill are sized to open.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SendFaultTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static readonly string ErrorQueue = $"{MessagingRegistration.EventsQueue}_error";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_relay_that_stops_leaves_its_row_pending_and_sends_it_when_it_returns()
    {
        await using Mailpit relay = Mailpit.PlainOn(FreeLoopbackPort());
        await relay.StartAsync(Ct);
        using NotificationsWorkerFactory host = fixture.NewWorkerHost(relay.Host, relay.Port);
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromMinutes(1));
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.WaitUntilDueAsync(order);
        await relay.StopAsync(Ct);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 0));

        Notification waiting = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1);
        waiting.SendStartedAt.ShouldNotBeNull("the intent precedes every send, a failed one included");
        (await fixture.QueueDepthAsync(ErrorQueue)).ShouldBe(0, "a dead relay is no consumer's fault (ADR-052)");

        await relay.StartAsync(Ct);
        await fixture.ClearBackoffAsync(waiting.NotificationId);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 1));

        (await fixture.NotificationAsync(waiting.NotificationId)).Status.ShouldBe(NotificationStatus.Sent);
        (await relay.SingleAsync(Ct)).MessageId.Trim('<', '>')
            .ShouldBe($"{waiting.EventId:N}.{TemplateKeys.OrderPlaced}@commerce.test");
    }

    [Fact]
    public async Task A_relay_refusing_every_attempt_opens_the_breaker_and_the_next_pass_claims_nothing()
    {
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        await fixture.Relay.RefuseRecipientsAsync(451, Ct);
        await OwedAsync(Guid.CreateVersion7());
        await OwedAsync(Guid.CreateVersion7());

        (await PassAsync(host)).ShouldBe(new SendPass(2, 0));
        host.Services.GetRequiredService<MailPipeline>().IsOpen
            .ShouldBeTrue("two sends of two attempts each reach MailHop's throughput, every one failed");

        // Healthy again, so a send that left the process now would be delivered.
        await fixture.Relay.ResetAsync(Ct);
        Notification parked = await OwedAsync(Guid.CreateVersion7());

        (await PassAsync(host)).ShouldBe(new SendPass(0, 0), "an open breaker parks the claim, not the messages");

        Notification untouched = await fixture.NotificationAsync(parked.NotificationId);
        untouched.Status.ShouldBe(NotificationStatus.Pending);
        untouched.Attempts.ShouldBe(0, "a row no pass claimed is not backed off");
        untouched.LockedUntil.ShouldBeNull();
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
        (await fixture.QueueDepthAsync(ErrorQueue)).ShouldBe(0);
    }

    [Fact]
    public async Task A_stale_contact_is_served_while_the_owner_cannot_answer()
    {
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, 503);
        await OwedAsync(order, customer);
        using OutboundCount refused = OutboundCounter.ContactRefused(host.Services);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 1));

        (await fixture.Relay.SingleAsync(Ct)).To.ShouldHaveSingleItem().Address.ShouldBe(Mailbox);
        fixture.ContactCalls(customer).ShouldBe(ContactHop.MaxRetryAttempts + 1, "the owner was asked, and retried");
        refused.Value.ShouldBe(0, "an outage is not a refused credential");
        host.CapturedLogs.Everything.ShouldContain(line => line.Contains("could not answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_contact_past_its_stale_ceiling_is_never_served_and_the_row_waits_for_the_owner()
    {
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromHours(25));
        fixture.ContactAnswers(customer, 503);
        Notification owed = await OwedAsync(order, customer);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 0));

        Notification waiting = await fixture.NotificationAsync(owed.NotificationId);
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1);
        waiting.SendStartedAt.ShouldBeNull("nothing was rendered without a contact");
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refusing_owner_backs_the_row_off_serves_no_stored_contact_and_is_counted_until_it_answers()
    {
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, 403);
        Notification owed = await OwedAsync(order, customer);
        using OutboundCount refused = OutboundCounter.ContactRefused(host.Services);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 0));

        Notification waiting = await fixture.NotificationAsync(owed.NotificationId);
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1);
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty("a refusal never proceeds on a stale row (ADR-052)");
        refused.Value.ShouldBe(1);
        host.CapturedLogs.Everything.ShouldContain(
            line => line.Contains("refused over this host's credential", StringComparison.Ordinal));
        (await fixture.QueueDepthAsync(ErrorQueue)).ShouldBe(0);

        fixture.Keycloak.Reset();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.ClearBackoffAsync(owed.NotificationId);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 1));
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task No_line_the_worker_logs_holds_the_mailbox_or_the_message()
    {
        // Every line carrying an exception: the owner's outage, the relay's refusal, the resend over the intent.
        const string Private = "aigerim.private@example.test";
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Private, "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, 503);
        await fixture.Relay.RefuseRecipientsAsync(451, Ct);
        Notification owed = await OwedAsync(order, customer);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 0));

        await fixture.Relay.ResetAsync(Ct);
        await fixture.ClearBackoffAsync(owed.NotificationId);
        (await PassAsync(host)).ShouldBe(new SendPass(1, 1));

        MailpitMessage sent = await fixture.Relay.SingleAsync(Ct);
        string[] captured = [.. host.CapturedLogs.Everything, .. fixture.CapturedLogs.Everything];

        captured.ShouldContain(
            line => line.StartsWith(typeof(MailUnavailableException).FullName!, StringComparison.Ordinal),
            "a capture that dropped the relay's exception would search none of it");
        captured.ShouldContain(
            line => line.Contains("could not answer", StringComparison.Ordinal),
            "a capture that missed the stale contact's line would search none of the owner's exception");
        captured.ShouldContain(
            line => line.Contains("sent again under the same Message-ID", StringComparison.Ordinal),
            "a capture that missed the resend would search none of the second send");

        captured.ShouldNotContain(line => line.Contains("aigerim.private", StringComparison.OrdinalIgnoreCase));
        captured.ShouldNotContain(line => line.Contains(sent.Subject, StringComparison.Ordinal));
        (await fixture.QueueDepthAsync(ErrorQueue)).ShouldBe(0, "no consumer faulted, so no Fault<T> carries anything");
    }

    private static (Guid Order, Guid Customer) Ids() => (Guid.CreateVersion7(), Guid.CreateVersion7());

    private static Task<SendPass> PassAsync(NotificationsWorkerFactory host) =>
        host.Services.GetRequiredService<SendWorker>().RunOnceAsync(Ct);

    // Bound and released here, so Docker can bind it next; a port another process takes between is a rerun.
    private static int FreeLoopbackPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();

        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>A confirmed order's notice for a customer with a fresh contact, unless one is given.</summary>
    private async Task<Notification> OwedAsync(Guid order, Guid? customer = null)
    {
        Guid owner = customer ?? Guid.CreateVersion7();
        if (customer is null)
            await fixture.StageContactAsync(owner, Mailbox, "en", TimeSpan.FromMinutes(1));

        await fixture.OrderAsync(order, owner);

        return await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
    }
}
