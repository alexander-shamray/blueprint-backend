using Common.Infrastructure.Identity;
using Common.Web;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§15.4's <c>ValidateOnStart</c>: the host refuses to boot without each credential.</summary>
public class OptionsValidationTests
{
    /// <summary>A field, since CA1861 is an error under ADR-019.</summary>
    private static readonly string[] Members = ["ClientId", "ClientSecret", "Scope"];

    /// <summary>Hosts started on one blank credential, re-asked past a disposal race that hides the reason.</summary>
    private const int StartAttempts = 5;

    /// <summary>The host's own registration refuses the blank credential, and its failure names the member.</summary>
    [Theory]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Scope")]
    public void The_host_refuses_to_start_without_each_credential_and_names_it(string member)
    {
        OptionsValidationException refusal = Refusal(member).ShouldBeOfType<OptionsValidationException>();

        refusal.OptionsType.ShouldBe(typeof(ServiceIdentityOptions));
        refusal.Message.ShouldContain(member);
    }

    /// <summary>Starts hosts missing one credential until one says why, not that its provider is disposed.</summary>
    /// <remarks>
    /// A failed start can dispose the provider the factory still reads, reporting <see cref="ObjectDisposedException"/>
    /// with no inner exception; only a failed start does that, so a re-ask cannot invent a refusal.
    /// </remarks>
    private static Exception Refusal(string member)
    {
        Exception? refusal = null;

        for (int attempt = 0; attempt < StartAttempts; attempt++)
        {
            using MissingSettingFactory factory = new(member);

            // The factory builds the host on first use.
            refusal = Record.Exception(() => factory.CreateClient())
                .ShouldNotBeNull("a blank credential must stop the host");

            if (refusal is not ObjectDisposedException { ObjectName: nameof(IServiceProvider) })
                return refusal;
        }

        throw new InvalidOperationException(
            "Every host started here reported a disposed provider, so none of them said why it refused to start.",
            refusal);
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
            .. Members.Select(name =>
                new KeyValuePair<string, string?>(
                    $"{ServiceIdentityOptions.SectionName}:{name}",
                    string.Equals(name, member, StringComparison.Ordinal) ? "" : "supplied"))
        ];
    }
}
