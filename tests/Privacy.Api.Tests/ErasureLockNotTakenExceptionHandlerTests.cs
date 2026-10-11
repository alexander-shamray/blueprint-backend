using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Privacy.Application.ErasureRequests;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>A lock still held after the wait is a 503 a client may retry, not a 500 (§10.5).</summary>
public class ErasureLockNotTakenExceptionHandlerTests
{
    [Fact]
    public async Task A_lock_not_taken_becomes_a_503_with_retry_after_and_its_own_code()
    {
        using IHost host = await StartThrowingAsync(
            new ErasureLockNotTakenException("privacy.erasure.request.x", new InvalidOperationException()));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/x", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(5));
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("code").GetString().ShouldBe("erasure_request.busy");
        body.RootElement.GetProperty("detail").GetString()!.ShouldNotContain("privacy.erasure");
    }

    [Fact]
    public async Task Any_other_exception_still_falls_through_to_the_500()
    {
        // The handler selects: one matching everything would pass the test above.
        using IHost host = await StartThrowingAsync(new InvalidOperationException("boom"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/x", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static Task<IHost> StartThrowingAsync(Exception exception) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddProblemDetails();
                    services.AddExceptionHandler<ErasureLockNotTakenExceptionHandler>();
                });
                web.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.Run(_ => throw exception);
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync();
}
