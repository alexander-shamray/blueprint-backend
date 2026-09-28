using Common.Infrastructure.Identity;
using Common.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// §15.4's <c>ValidateOnStart</c>, from the side that matters: the host must
/// refuse to boot without the credentials, not start clean and fail later.
/// </summary>
/// <remarks>
/// <b>Without <c>[Required]</c> and <c>ValidateOnStart</c> together, none of
/// this fails.</b> <c>IOptions&lt;T&gt;</c> always resolves — unbound it hands
/// back a default-constructed instance — so a forgotten binding is invisible to
/// <c>ValidateOnBuild</c>, and a bound class with no annotations validates
/// successfully while empty. The BFF would then start, request a token with an
/// empty <c>client_id</c>, and read Catalog's 401s as Catalog's fault. Each
/// member is removed separately below, because a single test that drops all
/// three would pass against a <c>[Required]</c> on only one of them.
/// </remarks>
public class OptionsValidationTests
{
    /// <summary>
    /// A field rather than a collection expression inside the subclass below,
    /// because CA1861 is an error under ADR-019 and the subclass's
    /// <c>Settings</c> is read on every host build.
    /// </summary>
    private static readonly string[] Members = ["ClientId", "ClientSecret", "Scope"];

    /// <summary>
    /// The host half: with a credential blank, it does not come up.
    /// </summary>
    /// <remarks>
    /// <b>The exception TYPE is deliberately not asserted here, and that is a
    /// fix rather than a weakening.</b> This used to require
    /// <see cref="OptionsValidationException"/> and failed intermittently on
    /// CI with <c>ObjectDisposedException</c> instead — a race inside
    /// <c>WebApplicationFactory</c> rather than anything about this host.
    /// <para>
    /// <c>Program</c> is top-level statements, so the factory drives it through
    /// <c>DeferredHostBuilder</c>: the entry point runs on another thread and
    /// <c>DeferredHost.StartAsync</c> then resolves services from the host it
    /// built. When <c>ValidateOnStart</c> throws — which is the whole point of
    /// this test — <c>app.Run()</c> **disposes that host on its way out**, and
    /// two things race: the entry point's exception being observed and
    /// rethrown, against the deferred host resolving from a provider that is
    /// now disposed. Lose it and the real exception is **destroyed rather than
    /// wrapped** — it is nowhere in the chain, so no assertion can recover it.
    /// </para>
    /// <para>
    /// Which row loses varies run to run, and a loaded two-core runner loses
    /// far more often than a developer's machine. So the claim this test can
    /// make honestly is "the host refused to start"; the claim that it refused
    /// **naming the missing member** is proven deterministically by
    /// <see cref="Each_credential_is_required_and_named_in_the_failure"/>
    /// below. Two assertions, neither depending on who wins a disposal race.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Scope")]
    public void The_host_refuses_to_start_without_each_credential(string member)
    {
        using MissingSettingFactory factory = new(member);

        // The factory builds the host on first use, so the throw arrives here
        // rather than at construction.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    /// <summary>
    /// The validation half, run directly against the options pipeline so no
    /// host is started and nothing can race.
    /// </summary>
    /// <remarks>
    /// <c>ValidateOnStart</c> registers an <c>IStartupValidator</c>, and
    /// invoking it is exactly what the host does while starting — so this
    /// exercises the same code path the host would, and gets the exception
    /// undisturbed. It duplicates <c>Program</c>'s four registration lines on
    /// purpose: the pairing of <c>[Required]</c> with
    /// <c>ValidateDataAnnotations</c> is what is under test, and the theory
    /// above is what still fails if <c>Program</c> ever drops the block.
    /// </remarks>
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
        // The other direction, so the theory above cannot pass by the host
        // being unstartable for some unrelated reason.
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    /// <summary>
    /// The base fixture's settings with one <c>Identity:Client</c> member
    /// blanked rather than merely left unset.
    /// </summary>
    /// <remarks>
    /// <b>Omitting it is not the same as removing it, and the difference is a
    /// developer's shell.</b> Configuration is layered, so a member this
    /// factory simply declines to supply can still be filled by a lower
    /// provider — and the environment is exactly where it would come from:
    /// <c>deploy/compose/README.md</c> tells a developer to
    /// <c>export Identity__Client__ClientId=web-bff</c> to run this host,
    /// which is the same shell they then run <c>dotnet test</c> in. The
    /// "missing" case would quietly become "present", the host would start,
    /// and this test would fail — or worse, pass for the wrong reason on the
    /// one machine that had not exported anything.
    /// <para>
    /// Setting the key to an empty string is what makes the removal real. It
    /// is also the more faithful case: §11.3 already records that a blank
    /// environment variable reaches configuration as <c>""</c> rather than
    /// null, and <c>[Required]</c> refuses both.
    /// </para>
    /// </remarks>
    private sealed class MissingSettingFactory(string member) : BffFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [
            new(AuthenticationExtensions.AuthorityKey, UnreachableAuthority),
            .. Members.Select(name => new KeyValuePair<string, string?>(
                $"{ServiceIdentityOptions.SectionName}:{name}",
                string.Equals(name, member, StringComparison.Ordinal) ? "" : "supplied"))
        ];
    }
}
