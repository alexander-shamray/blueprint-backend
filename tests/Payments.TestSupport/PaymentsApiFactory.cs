using Payments.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Payments.TestSupport;

/// <summary>
/// The real Payments host over caller-supplied dependencies (§12.4). One type
/// for both suites here — the host smoke points it at names that cannot
/// resolve, the container suite at running containers — so what differs
/// between them is the infrastructure and not the wiring.
/// </summary>
public class PaymentsApiFactory(string connectionString, string rabbitConnectionString)
    : WebApplicationFactory<Program>
{
    /// <summary>
    /// The authority every host over this <c>Program</c> must name (§11.3).
    /// Deliberately fake and deliberately unreachable — <c>.invalid</c> is
    /// reserved and never resolves, so a test that accidentally dials the
    /// authority fails loudly rather than reaching a real identity provider.
    /// </summary>
    /// <remarks>
    /// Required rather than optional for the same reason both connection
    /// strings are: <c>AddJwtAuthentication</c> reads this key eagerly and
    /// throws naming it, so a service host that cannot name its identity
    /// provider does not start. §12.4 attributed that failure to
    /// <c>ValidateOnStart</c> and <c>OptionsValidationException</c>, and the
    /// chapter was amended — §15.4 keeps <c>ServiceIdentityOptions</c> as the
    /// solution's only options type, so there is nothing here for
    /// <c>ValidateDataAnnotations</c> to check.
    /// </remarks>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>
    /// The RUNTIME connection of §7.1, and only that one. The host has no
    /// business reading <c>PaymentsMigrator</c>, and a fixture that supplied
    /// both would hide it if it started. The bus key is required because
    /// <c>AddMassTransitMessaging</c> throws without it — every host over
    /// this Program needs one, reachable or not.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:Payments", connectionString)
            .UseSetting("ConnectionStrings:RabbitMq", rabbitConnectionString)
            .UseSetting(AuthenticationExtensions.AuthorityKey, UnreachableAuthority)
            .ConfigureServices(services =>
            {
                ConfigureAuthentication(services);

                // Remove ONLY the outbox dispatcher, not every hosted service:
                // MassTransit registers its bus as one, so a
                // RemoveAll<IHostedService>() would stop the broker from
                // starting and silently disable every consumption test.
                //
                // The dispatcher polls every 500 ms; left running it drains
                // outbox rows underneath assertions about them. Tests that
                // want it call fixture.ProcessOutboxBatchAsync() explicitly.
                //
                // This match is why AddPaymentsInfrastructure uses
                // AddHostedService<T> rather than a factory overload — a
                // factory registration leaves ImplementationType null and
                // this line would quietly match nothing.
                ServiceDescriptor hosted = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(OutboxDispatcher));
                services.Remove(hosted);

                // Still resolvable directly, so tests can drive one pass.
                services.AddSingleton<OutboxDispatcher>();

                // §9.5's purge, removed and re-registered for the same two
                // reasons and by the same match. Its timer is an hour rather
                // than 500 ms, so it would not race an assertion in a run this
                // short — but a test asserting that an abandoned row survives
                // retention cannot be sure of that from a service it does not
                // drive, and "the pass never happened" and "the pass spared the
                // row" are the same green.
                ServiceDescriptor purge = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(RetentionPurgeService));
                services.Remove(purge);

                services.AddSingleton<RetentionPurgeService>();

                // §9.4. Adding, not replacing: the production assemblies stay,
                // so a test cannot stage a type the real host would refuse.
                // Without this, NameOf throws on the first builder call and
                // every outbox test fails before its assertion.
                //
                // Mutating the registered instance rather than re-registering
                // one, because MessageTypeSource is deliberately mutable for
                // exactly this and the map is built from it at first resolve.
                services
                    .Single(d => d.ServiceType == typeof(MessageTypeSource))
                    .ImplementationInstance
                    .ShouldBeSource()
                    .Add(typeof(AlwaysThrows).Assembly);

                // The projection handlers for two of those three events. Each
                // layer scans itself (§6.2), and this assembly is a layer the
                // production registration has no reason to know about.
                services.AddPluggableFrom(typeof(AlwaysThrows).Assembly);
            });

    /// <summary>
    /// Replaces the JWT scheme with <see cref="TestAuthHandler"/> (§12.4).
    /// Replacing rather than configuring: the endpoints under test are behind
    /// <c>RequireAuthorization</c> (§11.4), and the alternative is either a 401
    /// on every call or a fixture that fetches OIDC metadata over the network
    /// from an authority that is unreachable on purpose.
    /// </summary>
    /// <remarks>
    /// Virtual, and the one override matters. A host that keeps the production
    /// scheme is the only thing that can prove <see cref="TestAuthHandler"/>'s
    /// headers mean nothing to a real deployment, so it arrives with the first
    /// endpoint there is anything to forge against. A flag would say the same
    /// thing; a method says it at the site that makes the decision, which is
    /// where the argument for it belongs.
    ///
    /// Only the authenticate and challenge schemes are set, and forbid follows
    /// the challenge one: <c>DefaultForbidScheme</c> is unset, and
    /// <c>AuthenticationSchemeProvider</c> falls back to
    /// <c>DefaultChallengeScheme</c> before <c>DefaultScheme</c>. So the 403 is
    /// answered by <see cref="TestAuthHandler"/>'s inherited forbid — a bare
    /// status code, no metadata — and the wrong-permission test needs no
    /// identity provider either. Measured by resolving the provider rather
    /// than assumed: this comment previously credited the bearer handler,
    /// which never sees a forbid here.
    /// </remarks>
    protected virtual void ConfigureAuthentication(IServiceCollection services)
    {
        services.Configure<AuthenticationOptions>(o =>
        {
            o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
            o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
        });

        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
    }
}

file static class ServiceDescriptorExtensions
{
    /// <summary>
    /// Reads the registered instance back as itself, with a message that says
    /// what changed if it ever stops being registered that way — a cast
    /// failing here would otherwise read as a null reference from a line that
    /// mentions no null.
    /// </summary>
    public static MessageTypeSource ShouldBeSource(this object? instance) =>
        instance as MessageTypeSource ??
            throw new InvalidOperationException(
                "MessageTypeSource is no longer registered as a singleton instance, so the test " +
                "assembly's events cannot be added to it before the map is built (§9.4).");
}
