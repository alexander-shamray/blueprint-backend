using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Common.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What the authentication middleware does, and what an explicit call to it is worth.</summary>
/// <remarks>
/// §4.2 owns the argument: <c>WebApplication</c> adds the middleware itself, so an explicit call sets
/// order, not presence, and this suite is the regression guard if a release stops auto-inserting it.
/// </remarks>
public class AuthenticationMiddlewareTests
{
    // Not Scheme, which inside ProbeHandler binds to AuthenticationHandler<T>'s inherited property.
    private const string SchemeName = "Probe";

    private static readonly Guid Subject = Guid.CreateVersion7();

    [Fact]
    public async Task The_middleware_is_what_puts_the_principal_on_the_context()
    {
        // §11.4's pairing: the middleware populates HttpContext.User and ICurrentUser reads it.
        Probe probe = await ExplicitPipelineAsync(useAuthentication: true);

        probe.Status.ShouldBe(HttpStatusCode.OK);
        probe.Authenticated.ShouldBeTrue();
        probe.Id.ShouldBe(Subject.ToString());
    }

    [Fact]
    public async Task Authorization_does_not_authenticate_on_its_own()
    {
        // The authorization middleware evaluates HttpContext.User and does not populate it.
        Probe probe = await ExplicitPipelineAsync(useAuthentication: false);

        probe.Status.ShouldBe(HttpStatusCode.Unauthorized);
        probe.Authenticated.ShouldBeFalse("the handler is never reached");
    }

    [Fact]
    public async Task A_web_application_host_adds_the_middleware_without_being_asked()
    {
        // This host calls neither UseAuthentication nor UseAuthorization, and does both (§4.2).
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        Register(builder.Services);

        await using WebApplication app = builder.Build();
        MapProbe(app);

        await app.StartAsync(TestContext.Current.CancellationToken);

        Probe probe = await SendAsync(app.GetTestClient());

        await app.StopAsync(TestContext.Current.CancellationToken);

        probe.Status.ShouldBe(HttpStatusCode.OK);
        probe.Authenticated.ShouldBeTrue(
            "WebApplication auto-adds the authentication middleware — if this ever fails, every " +
            "service host is handing anonymous callers to its handlers and no other test can see it");
    }

    [Fact]
    public async Task But_it_does_not_repair_the_two_being_written_in_the_wrong_order()
    {
        // Auto-insertion repairs an omission, not an ordering (§4.2).
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        Register(builder.Services);

        await using WebApplication app = builder.Build();

        app.UseAuthorization();
        app.UseAuthentication();
        MapProbe(app);

        await app.StartAsync(TestContext.Current.CancellationToken);

        Probe probe = await SendAsync(app.GetTestClient());

        await app.StopAsync(TestContext.Current.CancellationToken);

        probe.Status.ShouldBe(HttpStatusCode.Unauthorized);
        probe.Authenticated.ShouldBeFalse("the handler is never reached");
    }

    /// <summary>One request through a hand-built pipeline, as a <c>WebApplication</c> adds the line back.</summary>
    private static async Task<Probe> ExplicitPipelineAsync(bool useAuthentication)
    {
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    Register(services);
                });

                web.Configure(app =>
                {
                    app.UseRouting();

                    // The one line under test.
                    if (useAuthentication)
                        app.UseAuthentication();

                    app.UseAuthorization();

                    app.UseEndpoints(MapProbe);
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);

        return await SendAsync(host.GetTestClient());
    }

    private static void Register(IServiceCollection services)
    {
        services
            .AddAuthentication(SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ProbeHandler>(SchemeName, _ => { });
        services.AddAuthorization();

        // The pairing AddCommonWebDefaults registers (§11.4), without its observability pipeline.
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
    }

    /// <summary>Reports what <see cref="ICurrentUser"/> saw, behind the default policy.</summary>
    private static void MapProbe(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet(
                "/",
                (ICurrentUser user) =>
                    Results.Ok(new ProbeBody(user.IsAuthenticated, user.IsAuthenticated ? user.Id.ToString() : null)))
            .RequireAuthorization();

    private static async Task<Probe> SendAsync(HttpClient client)
    {
        using (client)
        {
            HttpRequestMessage request = new(HttpMethod.Get, "/");
            request.Headers.Add(ProbeHandler.SubjectHeader, Subject.ToString());

            HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            if (response.StatusCode is not HttpStatusCode.OK)
                return new Probe(response.StatusCode, false, null);

            ProbeBody body = (await response.Content.ReadFromJsonAsync<ProbeBody>(
                TestContext.Current.CancellationToken))!;

            return new Probe(response.StatusCode, body.Authenticated, body.Id);
        }
    }

    /// <summary>What one request saw: the wire's answer, and the handler's.</summary>
    private sealed record Probe(HttpStatusCode Status, bool Authenticated, string? Id);

    private sealed record ProbeBody(bool Authenticated, string? Id);

    /// <summary>Authenticates whatever subject the request names: a JWT handler without the signature.</summary>
    private sealed class ProbeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SubjectHeader = "X-Probe-Subject";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims = [new(ClaimTypes.NameIdentifier, Request.Headers[SubjectHeader].ToString())];
            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));

            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
