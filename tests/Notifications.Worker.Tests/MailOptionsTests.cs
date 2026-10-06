using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notifications.Infrastructure.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay's registration and options refuse to send in the clear or unauthenticated (§15.4).</summary>
public sealed class MailOptionsTests
{
    [Fact]
    public void Outside_development_plain_submission_is_refused_at_registration()
    {
        Should
            .Throw<InvalidOperationException>(() => Bound(Environments.Production, Relay(security: "None")))
            .Message.ShouldContain($"{MailOptions.SecurityKey} is None outside Development");
    }

    [Theory]
    [InlineData(null, NotificationsWorkerFactory.NotARelayPassword)]
    [InlineData("notifications", null)]
    [InlineData(null, null)]
    public void Outside_development_a_missing_credential_is_refused_at_registration(string? userName, string? password)
    {
        Should.Throw<InvalidOperationException>(() =>
                Bound(Environments.Production, Relay(userName: userName, relayPassword: password)))
            .Message.ShouldContain(
                $"{MailOptions.UserNameKey} and {MailOptions.PasswordKey} are required outside Development");
    }

    [Fact]
    public void Outside_development_starttls_with_a_credential_is_accepted()
    {
        // The control for the two above, so they cannot pass against a registration nothing satisfies.
        using ServiceProvider provider = Bound(Environments.Production, Relay());

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void The_pipeline_builds_over_the_hops_numbers()
    {
        // Polly validates each strategy's options in Build(), which nothing else outside a container reaches.
        ServiceCollection services = Registered(Environments.Production, Relay());
        using ServiceProvider provider = services.BuildServiceProvider();

        // The pipeline is internal, so its type is read from the registration rather than named.
        Type pipeline = services.Single(d => d.ServiceType.Name == "MailPipeline").ServiceType;

        Should.NotThrow(() => provider.GetRequiredService(pipeline));
    }

    [Fact]
    public void Outside_development_a_host_over_a_plain_relay_does_not_start()
    {
        // The factory's defaults are Development's: plain, anonymous, which Program.cs must refuse elsewhere.
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit);
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        Should
            .Throw<InvalidOperationException>(() => production.Services)
            .Message.ShouldContain("is None outside Development");
    }

    [Fact]
    public void In_development_plain_unauthenticated_submission_is_accepted()
    {
        using ServiceProvider provider = Bound(
            Environments.Development,
            Relay(security: "None", userName: null, relayPassword: null));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("notifications", null)]
    [InlineData(null, NotificationsWorkerFactory.NotARelayPassword)]
    public void A_user_name_and_a_password_come_together_in_every_environment(string? userName, string? password)
    {
        using ServiceProvider provider = Bound(
            Environments.Development,
            Relay(security: "None", userName: userName, relayPassword: password));

        Should
            .Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("are set together or not at all");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("a@example.test, b@example.test")]
    [InlineData("a@example.test\r\nBcc: b@example.test")]
    [InlineData("a@xn--zz.test")]
    [InlineData("a@xn--a-.test")]
    [InlineData("a@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.test")]
    public void A_sender_that_is_not_one_mailbox_stops_the_host(string from)
    {
        using ServiceProvider provider = Bound(Environments.Production, Relay(from: from));

        Should
            .Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("From");
    }

    [Theory]
    [InlineData("smtp://relay.example.test")]
    [InlineData("relay.example.test:587")]
    [InlineData(" ")]
    public void A_host_that_is_not_a_host_name_stops_the_host(string host)
    {
        using ServiceProvider provider = Bound(Environments.Production, Relay(host: host));

        Should
            .Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Host");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("")]
    public void A_port_outside_the_range_stops_the_host(string port)
    {
        using ServiceProvider provider = Bound(Environments.Production, Relay(port: port));

        Should
            .Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Port");
    }

    [Fact]
    public void A_host_that_names_a_relay_starts()
    {
        // The control for the fact below: the same unreachable hosts start, so a refusal there is the missing relay's.
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit);

        Should.NotThrow(() => factory.CreateClient());
    }

    [Fact]
    public void A_host_that_names_no_relay_does_not_start()
    {
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, mailHost: "");

        // The factory builds the host on first use, and a host refusing to
        // start races its disposal, so no exception type is asserted.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    private static Dictionary<string, string?> Relay(
        string host = "relay.example.test",
        string port = "587",
        string from = NotificationsWorkerFactory.LocalFrom,
        string security = "StartTls",
        string? userName = "notifications",
        string? relayPassword = NotificationsWorkerFactory.NotARelayPassword) =>
        new()
        {
            [MailOptions.HostKey] = host,
            [MailOptions.PortKey] = port,
            [MailOptions.FromKey] = from,
            [MailOptions.SecurityKey] = security,
            [MailOptions.UserNameKey] = userName,
            [MailOptions.PasswordKey] = relayPassword
        };

    // The production registration over the given environment; the host facts above are what still fail if
    // Program.cs drops the call.
    private static ServiceProvider Bound(string environment, Dictionary<string, string?> relay) =>
        Registered(environment, relay).BuildServiceProvider();

    private static ServiceCollection Registered(string environment, Dictionary<string, string?> relay)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(relay).Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddMetrics();
        services.AddSingleton(TimeProvider.System);
        services.AddMailChannel(configuration, new TestEnvironment { EnvironmentName = environment });

        return services;
    }
}
