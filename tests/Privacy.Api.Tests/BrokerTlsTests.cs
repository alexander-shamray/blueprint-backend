using System.Net.Security;
using Privacy.Infrastructure.Messaging;
using MassTransit;
using MassTransit.RabbitMqTransport.Configuration;
using MassTransit.Transports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>ADR-079's broker half, read from the settings MassTransit dials with.</summary>
public sealed class BrokerTlsTests
{
    private static async Task<RabbitMqHostSettings> Settings(string connectionString)
    {
        ServiceCollection services = new();
        services.AddMassTransitMessaging(new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", connectionString)])
            .Build());
        await using ServiceProvider provider = services.BuildServiceProvider();
        IBusInstance bus = provider.GetRequiredService<IBusInstance>();
        return ((IRabbitMqHostConfiguration)bus.HostConfiguration).Settings;
    }

    [Theory]
    [InlineData("amqps://guest:guest@privacy-rabbit.invalid")]
    [InlineData("amqps://guest:guest@privacy-rabbit.invalid:5673")]
    public async Task An_amqps_broker_is_dialled_over_tls_and_its_certificate_chain_verified(string connectionString)
    {
        RabbitMqHostSettings settings = await Settings(connectionString);

        settings.Ssl.ShouldBeTrue("MassTransit turns TLS on by port alone, so the scheme must ask for it");
        (settings.AcceptablePolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors)
            .ShouldBe(SslPolicyErrors.None, "MassTransit accepts an untrusted chain unless told not to");
        (settings.AcceptablePolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch)
            .ShouldBe(SslPolicyErrors.None);
        settings.SslServerName.ShouldBe("privacy-rabbit.invalid", "a blank name accepts any certificate's host");
    }

    [Fact]
    public async Task An_amqp_broker_is_left_as_its_string_says()
    {
        (await Settings("amqp://guest:guest@privacy-rabbit.invalid:5672")).Ssl.ShouldBeFalse();
    }
}
