using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// The one response security header this platform owns (§10.6), asserted on
/// both paths a response can leave by.
/// </summary>
public class SecurityHeadersTests
{
    // The literal, not SecurityHeadersExtensions' own constant: a test that
    // reads the constant cannot notice the constant changing.
    private const string Header = "X-Content-Type-Options";

    private static Task<IHost> StartAsync(RequestDelegate terminal) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddCommonProblemDetails());
                web.Configure(app =>
                {
                    app.UseSecurityHeaders();
                    app.UseExceptionHandler();
                    app.Run(terminal);
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Every_response_carries_nosniff()
    {
        using IHost host = await StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        HttpResponseMessage response =
            await host.GetTestClient().GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        response.Headers.GetValues(Header).ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task The_error_response_carries_it_too()
    {
        // The half that decides where this middleware writes from.
        // UseExceptionHandler CLEARS the response before it writes §10.5's
        // problem body, so a header assigned on the way in is gone from
        // exactly the 500 a caller-supplied value is most likely to be
        // reflected on. Registering an OnStarting callback survives that clear
        // — and this test is the only thing that says so.
        using IHost host = await StartAsync(_ => throw new InvalidOperationException("boom"));

        HttpResponseMessage response =
            await host.GetTestClient().GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Headers.GetValues(Header).ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task It_is_written_once_when_something_below_has_already_set_it()
    {
        // Two values on this header are read by some browsers as no header at
        // all, so the middleware assigns rather than appends. The case is
        // reachable: a proxy in front, or a handler that sets it itself.
        using IHost host = await StartAsync(context =>
        {
            context.Response.Headers.Append(Header, "nosniff");
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        HttpResponseMessage response =
            await host.GetTestClient().GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        response.Headers.GetValues(Header).ShouldHaveSingleItem();
    }
}
