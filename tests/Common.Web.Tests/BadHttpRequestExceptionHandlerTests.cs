using System.Net;
using System.Text;
using System.Text.Json;
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

/// <summary>§10.5's unreadable-request row: one answer in every environment, at the exception's own status.</summary>
public class BadHttpRequestExceptionHandlerTests
{
    private const string Route = "/stock";

    [Fact]
    public async Task An_unreadable_request_becomes_a_400()
    {
        using IHost host = await StartThrowingAsync(new BadHttpRequestException("Failed to read the body."));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [InlineData(StatusCodes.Status408RequestTimeout)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType)]
    public async Task The_status_is_the_one_the_exception_carries(int status)
    {
        using IHost host = await StartThrowingAsync(new BadHttpRequestException("Refused.", status));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(status, "the status is the exception's, not a flat 400");

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(status);
        body.RootElement.GetProperty("code").GetString().ShouldBe("request.unreadable");
    }

    [Fact]
    public async Task The_problem_names_itself_with_a_code_and_carries_no_errors_member()
    {
        using JsonDocument body = await BodyOfAsync(new BadHttpRequestException("Failed to read the body."));

        body.RootElement.GetProperty("code").GetString().ShouldBe("request.unreadable");
        body.RootElement
            .TryGetProperty("errors", out _)
            .ShouldBeFalse("errors is what marks a validation refusal, and this request never reached a validator");
    }

    [Fact]
    public async Task The_problem_carries_the_same_customisation_as_every_other_problem_response()
    {
        using JsonDocument body = await BodyOfAsync(new BadHttpRequestException("Failed to read the body."));

        body.RootElement.GetProperty("title").GetString().ShouldBe("Bad Request");
        body.RootElement.GetProperty("instance").GetString().ShouldBe($"GET {Route}");
        body.RootElement.TryGetProperty("traceId", out _).ShouldBeTrue();
        body.RootElement.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_body_echoes_nothing_of_the_exception_or_its_cause()
    {
        // Both messages are the framework's own forms, which quote a raw value and a fragment of the body.
        BadHttpRequestException refused = new(
            "Failed to bind parameter \"int page\" from \"s3cr3t-value\".",
            new JsonException("'q' is an invalid start of a value. Path: $ | LineNumber: 0."));
        using IHost host = await StartThrowingAsync(refused);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(Route, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        content.ShouldNotContain("s3cr3t-value");
        content.ShouldNotContain("invalid start");
        content.ShouldNotContain("page");
    }

    [Fact]
    public async Task A_client_that_cannot_accept_problem_json_still_gets_the_400()
    {
        using IHost host = await StartThrowingAsync(new BadHttpRequestException("Failed to read the body."));
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");

        HttpResponseMessage response = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_status_that_is_not_the_clients_fault_is_not_this_handlers()
    {
        using IHost host = await StartThrowingAsync(new BadHttpRequestException("Refused.", 500));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("request.unreadable");
    }

    [Fact]
    public async Task Any_other_exception_still_falls_through_to_the_500()
    {
        // The handler selects: one matching everything would pass the tests above.
        using IHost host = await StartThrowingAsync(new InvalidOperationException("boom"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task A_body_that_does_not_bind_is_the_same_400_in_every_environment(string environment)
    {
        using JsonDocument body = await BindingBodyOfAsync(environment, """{"onHand":"three"}""");

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(StatusCodes.Status400BadRequest);
        body.RootElement.GetProperty("code").GetString().ShouldBe("request.unreadable");
    }

    [Theory]
    [InlineData("""{"onHand":"three"}""", "#/onHand")]
    [InlineData("""{"lines":[{"quantity":1},{"quantity":"two"}]}""", "#/lines/1/quantity")]
    public async Task The_pointer_names_the_member_that_did_not_bind(string json, string expected)
    {
        using JsonDocument body = await BindingBodyOfAsync("Production", json);

        body.RootElement.GetProperty("pointer").GetString().ShouldBe(expected);
    }

    [Fact]
    public async Task The_pointer_spells_the_member_as_the_contract_does_rather_than_as_it_was_sent()
    {
        using JsonDocument body = await BindingBodyOfAsync("Production", """{"ONHAND":"three"}""");

        body.RootElement.GetProperty("pointer").GetString().ShouldBe("#/onHand");
    }

    [Theory]
    [InlineData("""{"map":{"s3cr3t-key":"x"}}""", "#/map")]
    [InlineData("""{"s3cr3t-key":[1, }""", null)]
    public async Task The_pointer_stops_at_the_first_name_the_contract_does_not_declare(string json, string? expected)
    {
        string content = (await PostAsync("Production", json)).Content;

        // A dictionary key and an undeclared member are the client's content, not the endpoint's contract.
        content.ShouldNotContain("s3cr3t-key");

        using JsonDocument body = JsonDocument.Parse(content);
        body.RootElement.GetProperty("code").GetString().ShouldBe("request.unreadable");
        if (expected is null)
            body.RootElement.TryGetProperty("pointer", out _).ShouldBeFalse();
        else
            body.RootElement.GetProperty("pointer").GetString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    public async Task A_missing_body_is_the_same_400_with_no_pointer(string? json)
    {
        using JsonDocument body = await BindingBodyOfAsync("Production", json);

        body.RootElement.GetProperty("code").GetString().ShouldBe("request.unreadable");
        body.RootElement.TryGetProperty("pointer", out _).ShouldBeFalse("there is no member to point at");
    }

    [Fact]
    public async Task A_body_sent_with_no_content_type_is_a_415_with_the_same_code()
    {
        using IHost host = await StartBindingAsync("Production");
        using HttpClient client = host.GetTestClient();
        using StringContent content = new("""{"onHand":3}""");
        content.Headers.ContentType = null;

        using HttpResponseMessage response = await client.PostAsync(
            Route,
            content,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("code").GetString().ShouldBe("request.unreadable");
    }

    [Fact]
    public async Task A_query_value_that_does_not_bind_is_the_same_400_with_no_pointer()
    {
        using IHost host = await StartBindingAsync("Production");
        using HttpClient client = host.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(
            $"{Route}?page=three",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("code").GetString().ShouldBe("request.unreadable");
        body.RootElement.TryGetProperty("pointer", out _).ShouldBeFalse("a pointer names a member of the body");
    }

    private static async Task<JsonDocument> BodyOfAsync(Exception exception)
    {
        using IHost host = await StartThrowingAsync(exception);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        // The 500 fallback writes through the same service, so only the status shows this handler answered.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<JsonDocument> BindingBodyOfAsync(string environment, string? json)
    {
        Answer answer = await PostAsync(environment, json);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.MediaType.ShouldBe("application/problem+json");

        return JsonDocument.Parse(answer.Content);
    }

    private static async Task<Answer> PostAsync(string environment, string? json)
    {
        using IHost host = await StartBindingAsync(environment);
        using HttpClient client = host.GetTestClient();

        using HttpResponseMessage response = await client.PostAsync(
            Route,
            json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        return new Answer(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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
            .StartAsync(TestContext.Current.CancellationToken);

    /// <summary>A real minimal-API endpoint, since the framework's own binding is what raises the exception.</summary>
    private static Task<IHost> StartBindingAsync(string environment) =>
        new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting().AddCommonProblemDetails());
                web.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost(Route, (StockRequest _) => Results.NoContent());
                        endpoints.MapGet(Route, (int page) => Results.NoContent());
                    });
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);

    private sealed record Answer(HttpStatusCode Status, string? MediaType, string Content);

    private sealed record StockRequest(int? OnHand, List<StockLine>? Lines, Dictionary<string, int>? Map);

    private sealed record StockLine(int Quantity);
}
