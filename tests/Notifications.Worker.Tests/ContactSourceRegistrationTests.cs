using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notifications.Infrastructure.Contacts;
using Notifications.TestSupport;
using Shouldly;
using Xunit;
using ContactRegistration = Notifications.Infrastructure.Contacts.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>The contact source's two keys, refused at registration so a host that cannot read never starts.</summary>
public sealed class ContactSourceRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("keycloak/")]
    [InlineData("ftp://keycloak.example/")]
    public void A_base_address_that_is_not_an_absolute_http_one_is_refused_by_key(string? configured)
    {
        Should.Throw<InvalidOperationException>(() => Register(configured, Environments.Development))
            .Message.ShouldContain(ContactRegistration.BaseUrlKey);
    }

    [Theory]
    [InlineData("https://keycloak.example/?realm=master")]
    [InlineData("https://keycloak.example/#admin")]
    public void A_base_address_with_a_query_or_fragment_is_refused(string configured)
    {
        Should.Throw<InvalidOperationException>(() => Register(configured, Environments.Production))
            .Message.ShouldContain("query or fragment");
    }

    [Fact]
    public void A_base_address_carrying_user_information_is_refused_without_echoing_it()
    {
        string message = Should.Throw<InvalidOperationException>(
            () => Register("https://notifications:hunter2@keycloak.example/", Environments.Production)).Message;

        message.ShouldContain("user information");
        message.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Plain_http_outside_development_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => Register("http://keycloak.example/", Environments.Production))
            .Message.ShouldContain("plain HTTP outside Development");
    }

    [Fact]
    public void Https_outside_development_and_plain_http_in_it_are_each_accepted()
    {
        // The controls, so the two refusals above cannot pass against a rule nothing satisfies.
        Should.NotThrow(() => Register("https://keycloak.example/", Environments.Production));
        Should.NotThrow(() => Register("http://keycloak:8080/", Environments.Development));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("commerce/../master")]
    [InlineData("..")]
    [InlineData("comm erce")]
    [InlineData("%2e%2e")]
    [InlineData("commerce?x=1")]
    public void A_realm_that_is_not_one_plain_path_segment_is_refused_by_key(string? realm)
    {
        Should.Throw<InvalidOperationException>(
                () => Register("https://keycloak.example/", Environments.Production, realm))
            .Message.ShouldContain(ContactRegistration.RealmKey);
    }

    [Fact]
    public void A_host_that_names_no_contact_source_does_not_start()
    {
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, contactSourceBaseUrl: "");

        // Refused while Program registers, before a host exists to race its own disposal.
        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain(ContactRegistration.BaseUrlKey);
    }

    private static IServiceCollection Register(
        string? baseUrl,
        string environment,
        string? realm = NotificationsWorkerFactory.LocalRealm)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ContactRegistration.BaseUrlKey] = baseUrl,
                [ContactRegistration.RealmKey] = realm
            })
            .Build();

        TestEnvironment host = new() { EnvironmentName = environment };

        return new ServiceCollection().AddContactSource(configuration, host);
    }
}
