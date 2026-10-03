using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.Infrastructure.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay's fault rows, a host each, because the breaker they fill is sized to open.</summary>
[Collection(nameof(MailpitCollection))]
public sealed class MailFaultTests(MailpitFixture fixture) : IAsyncLifetime
{
    private NotificationsWorkerFactory _host = null!;

    public async ValueTask InitializeAsync()
    {
        await fixture.Plain.ResetAsync(TestContext.Current.CancellationToken);
        _host = MailpitFixture.Development(fixture.Plain);
    }

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    private IMailChannel Channel() => _host.Services.GetRequiredService<IMailChannel>();

    private static OutboundMail Mail(string recipient = "aigerim@example.test", MailMessageId? id = null) =>
        new(
            recipient,
            "Your order is placed",
            "Order 42 is placed.",
            id ?? new MailMessageId(Guid.CreateVersion7(), "order-placed"),
            ["en"]);

    // Plain submission with a credential, which Development allows, so the relay is asked to judge one.
    private static NotificationsWorkerFactory Credentialed(int port) =>
        new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: "127.0.0.1",
            mailPort: port,
            mailUserName: "notifications",
            mailPassword: NotificationsWorkerFactory.NotARelayPassword);

    [Fact]
    public async Task A_relay_declining_for_now_is_retried_then_thrown_as_unavailable_and_counted_per_attempt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(451, ct);
        using OutboundCount counted = OutboundCounter.Unavailable(_host.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            Channel().SendAsync(Mail(), ct));

        thrown.Cause.ShouldBe(MailFault.Transient);
        thrown.SmtpStatus.ShouldBe(451);
        counted.Of("transient").ShouldBe(
            MailHop.MaxRetryAttempts + 1,
            "one per attempt, and a 4xx before the data is the relay never having taken the message");
    }

    [Fact]
    public async Task A_sender_the_relay_refuses_for_good_backs_off_and_is_not_retried()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseSendersAsync(550, ct);
        using OutboundCount counted = OutboundCounter.Unavailable(_host.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            Channel().SendAsync(Mail(), ct));

        // This deployment's own From refused, which no customer's row should end on.
        thrown.Cause.ShouldBe(MailFault.Rejected);
        thrown.SmtpStatus.ShouldBe(550);
        counted.Of("rejected").ShouldBe(1, "a permanent refusal is not retried in the client");
    }

    [Fact]
    public async Task The_fault_names_the_message_and_never_the_mailbox_or_the_relays_words()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(451, ct);
        MailMessageId id = new(Guid.CreateVersion7(), "payment-declined");

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            Channel().SendAsync(Mail(recipient: "aigerim.private@example.test", id: id), ct));

        string everything = thrown.ToString();
        everything.ShouldContain(id.LocalPart);
        everything.ShouldNotContain("aigerim.private", Case.Insensitive, "§13.4: a fault carries an id alone");
        everything.ShouldNotContain("Chaos", Case.Insensitive, "the relay's reply text is the relay's words");
        thrown.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task A_refused_connection_is_unavailable_rather_than_a_refusal()
    {
        // A loopback port bound and released, so nothing listens and the connect is refused.
        TcpListener released = new(IPAddress.Loopback, 0);
        released.Start();
        int closed = ((IPEndPoint)released.LocalEndpoint).Port;
        released.Stop();

        using NotificationsWorkerFactory refusing = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: "127.0.0.1",
            mailPort: closed);
        using OutboundCount counted = OutboundCounter.Unavailable(refusing.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            refusing.Services.GetRequiredService<IMailChannel>()
                .SendAsync(Mail(), TestContext.Current.CancellationToken));

        thrown.Cause.ShouldBe(MailFault.Transient);
        thrown.Message.ShouldContain(nameof(SocketException), Case.Sensitive, "a refused connect, not a timeout");
        counted.Of("transient").ShouldBe(MailHop.MaxRetryAttempts + 1, "a refused connect is retried");
    }

    [Fact]
    public async Task A_relay_name_that_never_resolves_is_unavailable_rather_than_a_refusal()
    {
        // The factory's default relay, whose .invalid name never resolves.
        using NotificationsWorkerFactory unresolved = new(Unreachable.Sql, Unreachable.Rabbit);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            unresolved.Services.GetRequiredService<IMailChannel>()
                .SendAsync(Mail(), TestContext.Current.CancellationToken));

        thrown.Cause.ShouldBe(MailFault.Transient);
    }

    [Fact]
    public async Task A_relay_that_never_greets_is_unavailable_within_the_total_budget_and_its_timeouts_count()
    {
        // Accepts the connection into its backlog and never says a word, which is a relay that has hung.
        TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();

        try
        {
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            using NotificationsWorkerFactory host = new(
                Unreachable.Sql,
                Unreachable.Rabbit,
                mailHost: "127.0.0.1",
                mailPort: port);
            using OutboundCount counted = OutboundCounter.Unavailable(host.Services);
            long started = Stopwatch.GetTimestamp();

            await Should.ThrowAsync<MailUnavailableException>(() => host.Services
                .GetRequiredService<IMailChannel>()
                .SendAsync(Mail(), TestContext.Current.CancellationToken));

            Stopwatch.GetElapsedTime(started).ShouldBeLessThan(MailHop.TotalTimeout + TimeSpan.FromSeconds(2));
            counted.Of("transient").ShouldBeGreaterThanOrEqualTo(
                1,
                "an attempt timeout before the send is the relay's, counted by the pipeline's OnTimeout");
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public async Task A_relay_that_stalls_inside_the_envelope_is_unconfirmed_and_never_retried()
    {
        // Answers EHLO, then never answers MAIL FROM.
        await using ScriptedRelay stalled = new("250 relay.test");
        using NotificationsWorkerFactory host = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: "127.0.0.1",
            mailPort: stalled.Port);
        using OutboundCount counted = OutboundCounter.Unavailable(host.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() => host.Services
            .GetRequiredService<IMailChannel>()
            .SendAsync(Mail(), TestContext.Current.CancellationToken));

        // An attempt timeout once the send began may follow a relay that took the message, so a retry could send it
        // twice (§9.7): it is the adapter's fault, never a timeout the pipeline would convert and retry.
        thrown.Cause.ShouldBe(MailFault.Unconfirmed);
        stalled.Connections.ShouldBe(1, "a send the relay may hold is not made again in the client");
        counted.Of("unconfirmed").ShouldBe(1);
        counted.Of("transient").ShouldBe(0, "the attempt timeout's OnTimeout never saw a cancellation");
    }

    [Fact]
    public async Task The_callers_cancellation_during_the_send_is_not_counted_against_the_relay()
    {
        // Answers EHLO, then never answers MAIL FROM, so the send has begun when the caller gives up.
        await using ScriptedRelay stalled = new("250 relay.test");
        using NotificationsWorkerFactory host = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: "127.0.0.1",
            mailPort: stalled.Port);
        using OutboundCount counted = OutboundCounter.Unavailable(host.Services);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromSeconds(1));

        await Should.ThrowAsync<OperationCanceledException>(() => host.Services
            .GetRequiredService<IMailChannel>()
            .SendAsync(Mail(), caller.Token));

        counted.Value.ShouldBe(0, "a pass cancelled at shutdown is not a relay incident, whichever phase it was in");
        stalled.Connections.ShouldBe(1, "the send began, and was not made again");
    }

    [Fact]
    public async Task An_authentication_the_relay_declines_for_now_is_retried_under_its_code()
    {
        await using ScriptedRelay relay = new(
            "250-relay.test\r\n250 AUTH PLAIN",
            "454 4.7.0 Temporary authentication failure");
        using NotificationsWorkerFactory host = Credentialed(relay.Port);
        using OutboundCount counted = OutboundCounter.Unavailable(host.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() => host.Services
            .GetRequiredService<IMailChannel>()
            .SendAsync(Mail(), TestContext.Current.CancellationToken));

        thrown.Cause.ShouldBe(MailFault.Transient);
        thrown.SmtpStatus.ShouldBe(454);
        relay.Connections.ShouldBe(MailHop.MaxRetryAttempts + 1, "a 4xx to AUTH is the relay declining for now");
        counted.Of("transient").ShouldBe(MailHop.MaxRetryAttempts + 1);
        counted.Of("credential").ShouldBe(0, "the credential was never judged");
        thrown.ToString().ShouldNotContain("Temporary", Case.Insensitive, "the reply text is the relay's words");
    }

    [Fact]
    public async Task An_authentication_the_relay_refuses_for_good_is_a_credential_fault_and_is_not_retried()
    {
        await using ScriptedRelay relay = new(
            "250-relay.test\r\n250 AUTH PLAIN",
            "535 5.7.8 Authentication credentials invalid");
        using NotificationsWorkerFactory host = Credentialed(relay.Port);
        using OutboundCount counted = OutboundCounter.Unavailable(host.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() => host.Services
            .GetRequiredService<IMailChannel>()
            .SendAsync(Mail(), TestContext.Current.CancellationToken));

        thrown.Cause.ShouldBe(MailFault.Credential);
        thrown.SmtpStatus.ShouldBe(535);
        relay.Connections.ShouldBe(1, "a refused credential is a deployment's fault, which a retry cannot mend");
        counted.Of("credential").ShouldBe(1);
    }

    [Fact]
    public async Task An_open_circuit_makes_no_call_at_all()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(451, ct);
        using OutboundCount counted = OutboundCounter.Unavailable(_host.Services);

        while (counted.Value < MailHop.CircuitBreakerMinimumThroughput)
        {
            await Should.ThrowAsync<MailUnavailableException>(() => Channel().SendAsync(Mail(), ct));
        }

        // The relay is healthy again, so a send that left this process now would be delivered.
        await fixture.Plain.ResetAsync(ct);

        await Should.ThrowAsync<MailUnavailableException>(() => Channel().SendAsync(Mail(), ct));

        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty(
            "once open, the breaker refuses without a connection, which is what stops a pass hammering a dead relay");
    }
}
