using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Common.Application;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§10.5's customisation on the body <c>ToHttpResult</c> writes, with §10.4's correlation ID.</summary>
public class ProblemDetailsCompositionTests
{
    private static readonly Error Refused =
        Error.Rule("test.already_shipped", "A shipped order cannot be cancelled.");

    [Fact]
    public async Task A_failed_result_is_written_as_problem_json()
    {
        using IHost host = await TestPipeline.StartAsync(
            context => Result.Failure(Refused).ToHttpResult().ExecuteAsync(context));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        // Not application/json: a client recognises §10.5's one error shape by content type.
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task The_problem_body_carries_the_error_code_and_description()
    {
        using JsonDocument body = await FailingRequestAsync();

        body.RootElement.GetProperty("code").GetString().ShouldBe("test.already_shipped");
        body.RootElement.GetProperty("detail").GetString()
            .ShouldBe("A shipped order cannot be cancelled.");
    }

    [Fact]
    public async Task The_title_stays_the_status_phrase_rather_than_the_code()
    {
        using JsonDocument body = await FailingRequestAsync();

        // A client switching on title gets HTTP's vocabulary, not this service's.
        body.RootElement.GetProperty("title").GetString().ShouldBe("Unprocessable Entity");
    }

    [Fact]
    public async Task The_problem_body_carries_the_correlation_and_trace_ids()
    {
        using Activity activity = new Activity("incoming").Start();

        using JsonDocument body = await FailingRequestAsync();

        body.RootElement.GetProperty("correlationId").GetString()
            .ShouldBe(activity.TraceId.ToString());
        body.RootElement.GetProperty("traceId").GetString().ShouldBe(activity.Id);
    }

    [Fact]
    public async Task The_problem_instance_names_the_method_and_path()
    {
        using JsonDocument body = await FailingRequestAsync();

        // The path, not the type URI, since an incident starts from the request that failed.
        body.RootElement.GetProperty("instance").GetString().ShouldBe("GET /orders/cancel");
    }

    private static async Task<JsonDocument> FailingRequestAsync()
    {
        using IHost host = await TestPipeline.StartAsync(
            context => Result.Failure(Refused).ToHttpResult().ExecuteAsync(context));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
