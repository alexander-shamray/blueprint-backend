using Common.Infrastructure.Identity;
using Common.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§15.4's <c>ValidateOnStart</c>: the host refuses to boot without each credential.</summary>
public class OptionsValidationTests
{
    /// <summary>A field, since CA1861 is an error under ADR-019.</summary>
    private static readonly string[] Members = ["ClientId", "ClientSecret", "Scope"];

    /// <summary>Any exception, as a disposal race in the factory can replace the validation failure.</summary>
    /// <remarks>
    /// <see cref="Each_credential_is_required_and_named_in_the_failure"/> names the missing member.
    /// </remarks>
    [Theory]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Scope")]
    public void The_host_refuses_to_start_without_each_credential(string member)
    {
        using MissingSettingFactory factory = new(member);

        // The factory builds the host on first use.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    /// <summary>The startup validator the host runs, invoked directly so no host starts and nothing races.</summary>
    [Theory]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Scope")]
    public void Each_credential_is_required_and_named_in_the_failure(string member)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(Members.Select(name => new KeyValuePair<string, string?>(
                $"{ServiceIdentityOptions.SectionName}:{name}",
                string.Equals(name, member, StringComparison.Ordinal) ? "" : "supplied")))
            .Build());

        services
            .AddOptions<ServiceIdentityOptions>()
            .BindConfiguration(ServiceIdentityOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException thrown = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        thrown.Message.ShouldContain(member);
    }

    [Fact]
    public void The_host_starts_when_all_three_are_supplied()
    {
        // So the refusals cannot pass on a host unstartable for some other reason.
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    /// <summary>One member blanked rather than unset, so an exported environment variable cannot refill it.</summary>
    private sealed class MissingSettingFactory(string member) : BffFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [
            new(AuthenticationExtensions.AuthorityKey, UnreachableAuthority),
            new("ConnectionStrings:Bff", UnreachableDatabase),
            new("ConnectionStrings:RabbitMq", UnreachableBroker),
            .. Members.Select(name => new KeyValuePair<string, string?>(
                $"{ServiceIdentityOptions.SectionName}:{name}",
                string.Equals(name, member, StringComparison.Ordinal) ? "" : "supplied"))
        ];
    }
}
