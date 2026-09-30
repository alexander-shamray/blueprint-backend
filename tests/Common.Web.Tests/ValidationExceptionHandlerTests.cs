using System.Net;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§10.5's 400 row on the wire, in §4.2's pipeline shape with the exception handler outermost.</summary>
public class ValidationExceptionHandlerTests
{
    [Fact]
    public async Task A_validation_exception_becomes_a_400_with_field_keyed_errors()
    {
        ValidationFailure[] failures =
        [
            new("Name", "'Name' must not be empty."),
            new("Name", "'Name' is too short."),
            new("Amount", "'Amount' must not be negative.")
        ];
        using IHost host = await StartThrowingAsync(new ValidationException(failures));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/products", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement errors = body.RootElement.GetProperty("errors");
        errors.GetProperty("Name").GetArrayLength().ShouldBe(2);
        errors.GetProperty("Amount").GetArrayLength().ShouldBe(1);

        // §10.5's customisation reaches this body too.
        body.RootElement.GetProperty("instance").GetString().ShouldBe("GET /products");
        body.RootElement.TryGetProperty("traceId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_client_that_cannot_accept_problem_json_still_gets_the_400()
    {
        // Echoing TryWriteAsync's false would fall through to the 500; the status alone is the answer.
        ValidationFailure[] failures = [new("Name", "'Name' must not be empty.")];
        using IHost host = await StartThrowingAsync(new ValidationException(failures));
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");

        HttpResponseMessage response = await client.GetAsync("/products", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Any_other_exception_still_falls_through_to_the_500()
    {
        // A 400 for a genuine fault would blame the client for this service's bug.
        using IHost host = await StartThrowingAsync(new InvalidOperationException("boom"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/products", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static Task<IHost> StartThrowingAsync(Exception exception) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddCommonProblemDetails());
                web.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.Run(_ => throw exception);
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync();
}
