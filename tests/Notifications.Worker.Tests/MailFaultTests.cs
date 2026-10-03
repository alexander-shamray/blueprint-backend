using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
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

    [Fact]
    public async Task A_relay_declining_for_now_is_retried_then_thrown_as_unavailable_and_counted_per_attempt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(451, ct);
        using MailCount counted = MailCounter.Unavailable(_host.Services);

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
        using MailCount counted = MailCounter.Unavailable(_host.Services);

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
        // The factory's default relay, whose .invalid name never resolves.
        using NotificationsWorkerFactory unreachable = new(Unreachable.Sql, Unreachable.Rabbit);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            unreachable.Services.GetRequiredService<IMailChannel>()
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
            using MailCount counted = MailCounter.Unavailable(host.Services);
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
        await using EnvelopeStall stalled = new();
        using NotificationsWorkerFactory host = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: "127.0.0.1",
            mailPort: stalled.Port);
        using MailCount counted = MailCounter.Unavailable(host.Services);

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
    public async Task An_open_circuit_makes_no_call_at_all()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(451, ct);
        using MailCount counted = MailCounter.Unavailable(_host.Services);

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

    /// <summary>A relay that greets and answers <c>EHLO</c>, then never answers <c>MAIL FROM</c>.</summary>
    private sealed class EnvelopeStall : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private int _connections;

        public EnvelopeStall()
        {
            _listener.Start();
            _accepting = AcceptAsync(_stop.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref _connections);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accepting;
            _stop.Dispose();
        }

        private async Task AcceptAsync(CancellationToken ct)
        {
            List<Task> conversations = [];

            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                    Interlocked.Increment(ref _connections);
                    conversations.Add(ConverseAsync(client, ct));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            await Task.WhenAll(conversations);
        }

        private static async Task ConverseAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                    await stream.WriteAsync("220 relay.test ESMTP\r\n"u8.ToArray(), ct);
                    await reader.ReadLineAsync(ct);
                    await stream.WriteAsync("250 relay.test\r\n"u8.ToArray(), ct);

                    // Holds the connection open, unanswered, until the test is over.
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
                {
                }
            }
        }
    }
}
