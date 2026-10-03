using MailKit.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.Infrastructure.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>A session weaker than configured is refused before a credential or a message crosses it.</summary>
[Collection(nameof(MailpitCollection))]
public sealed class MailTlsTests(MailpitFixture fixture) : IAsyncLifetime
{
    // MailKit wraps whatever ends a handshake, so a trust refusal is told from a stall or a reset by what it wraps.
    private const string TrustRefused =
        $"{nameof(SslHandshakeException)} ({nameof(System.Security.Authentication.AuthenticationException)})";

    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.ResetAsync(ct);
        await fixture.SelfSigned.ResetAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static OutboundMail Mail() =>
        new(
            "aigerim@example.test",
            "Your order is placed",
            "Order 42 is placed.",
            new MailMessageId(Guid.CreateVersion7(), "order-placed"),
            ["en"]);

    private static NotificationsWorkerFactory StartTls(string host, int port) =>
        new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: host,
            mailPort: port,
            mailSecurity: "StartTls",
            mailUserName: "notifications",
            mailPassword: NotificationsWorkerFactory.NotARelayPassword);

    [Fact]
    public async Task Outside_development_a_relay_whose_certificate_nothing_trusts_is_refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using NotificationsWorkerFactory factory = StartTls(Mailpit.CertificateName, fixture.SelfSigned.Port);
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using OutboundCount counted = OutboundCounter.Unavailable(production.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            production.Services.GetRequiredService<IMailChannel>().SendAsync(Mail(), ct));

        thrown.Cause.ShouldBe(MailFault.Tls);
        thrown.Message.ShouldContain(TrustRefused, Case.Sensitive, "the trust store refused the certificate");
        counted.Of("tls").ShouldBe(1, "a TLS refusal is a deployment's decision: counted apart, and not retried");
        (await fixture.SelfSigned.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Outside_development_a_relay_offering_no_starttls_is_refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using NotificationsWorkerFactory factory = StartTls(fixture.Plain.Host, fixture.Plain.Port);
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            production.Services.GetRequiredService<IMailChannel>().SendAsync(Mail(), ct));

        // A relay that stops offering STARTTLS is the downgrade the setting exists to refuse.
        thrown.Cause.ShouldBe(MailFault.Tls);
        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task In_development_the_untrusted_certificate_is_refused_too()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using NotificationsWorkerFactory development = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: Mailpit.CertificateName,
            mailPort: fixture.SelfSigned.Port,
            mailSecurity: "StartTls");

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            development.Services.GetRequiredService<IMailChannel>().SendAsync(Mail(), ct));

        // Development relaxes None and the credential, and never the trust store.
        thrown.Cause.ShouldBe(MailFault.Tls);
        thrown.Message.ShouldContain(TrustRefused, Case.Sensitive, "the trust store refused the certificate");
    }

    [Fact]
    public async Task A_handshake_that_stalls_is_a_retried_timeout_and_never_a_tls_refusal()
    {
        // Offers STARTTLS and agrees to it, then never answers the client's hello.
        await using ScriptedRelay stalled = new("250-relay.test\r\n250 STARTTLS", "220 2.0.0 Ready to start TLS");
        using NotificationsWorkerFactory development = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: "127.0.0.1",
            mailPort: stalled.Port,
            mailSecurity: "StartTls");
        using OutboundCount counted = OutboundCounter.Unavailable(development.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() => development.Services
            .GetRequiredService<IMailChannel>()
            .SendAsync(Mail(), TestContext.Current.CancellationToken));

        thrown.Cause.ShouldBe(MailFault.Transient);
        stalled.Connections.ShouldBe(MailHop.MaxRetryAttempts + 1, "a timeout before the send is retried");
        counted.Of("transient").ShouldBe(MailHop.MaxRetryAttempts + 1, "each attempt timeout, counted by OnTimeout");
        counted.Of("tls").ShouldBe(0, "a stalled relay is an outage, not a session weaker than configured");
    }
}
