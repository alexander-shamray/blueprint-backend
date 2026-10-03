using MailKit.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>A session weaker than configured is refused before a credential or a message crosses it.</summary>
[Collection(nameof(MailpitCollection))]
public sealed class MailTlsTests(MailpitFixture fixture) : IAsyncLifetime
{
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
        using MailCount counted = MailCounter.Unavailable(production.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            production.Services.GetRequiredService<IMailChannel>().SendAsync(Mail(), ct));

        thrown.Cause.ShouldBe(MailFault.Tls);
        thrown.Message.ShouldContain(nameof(SslHandshakeException), Case.Sensitive, "STARTTLS was offered and refused");
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
        thrown.Message.ShouldContain(nameof(SslHandshakeException), Case.Sensitive, "STARTTLS was offered and refused");
    }
}
