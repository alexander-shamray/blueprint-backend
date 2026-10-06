using System.Globalization;
using System.Text.Json;
using Common.TestSupport;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>An endpoint's declared example in its OpenAPI document, and the rule a host's suite holds it to.</summary>
public class RequestExampleTests
{
    public sealed record Restock(Guid ProductId, int Quantity);

    public sealed record Untyped(string Note);

    private sealed class RestockValidator : AbstractValidator<Restock>
    {
        public RestockValidator()
        {
            RuleFor(r => r.Quantity).GreaterThan(0);
        }
    }

    private static readonly Restock Valid = new(Guid.Parse("0198f3a2-4b1c-7d2e-9f30-5a6b7c8d9e0f"), 3);

    [Fact]
    public async Task The_document_carries_the_declared_example_in_the_hosts_own_spelling()
    {
        using IHost host = await StartAsync(endpoints =>
            endpoints.MapPost("/restock", (Restock _) => Results.NoContent()).WithRequestExample(Valid));

        using JsonDocument document = await DocumentAsync(host);
        JsonElement example = document.RootElement
            .GetProperty("paths")
            .GetProperty("/restock")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("example");

        example.GetProperty("productId").GetGuid().ShouldBe(Valid.ProductId);
        example.GetProperty("quantity").GetInt32().ShouldBe(Valid.Quantity);
    }

    [Fact]
    public async Task An_endpoint_that_declares_none_gets_none()
    {
        using IHost host = await StartAsync(endpoints =>
            endpoints.MapPost("/restock", (Restock _) => Results.NoContent()));

        using JsonDocument document = await DocumentAsync(host);

        document.RootElement
            .GetProperty("paths")
            .GetProperty("/restock")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .TryGetProperty("example", out _)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task A_bodied_endpoint_with_no_example_is_an_offender()
    {
        using IHost host = await StartAsync(endpoints =>
            endpoints.MapPost("/restock", (Restock _) => Results.NoContent()).WithName("Restock"));

        RequestExampleRule
            .Offenders(Endpoints(host), host.Services)
            .ShouldHaveSingleItem()
            .ShouldBe("Restock binds a Restock and declares no example: add .WithRequestExample(...)");
    }

    [Fact]
    public async Task An_example_of_another_type_is_an_offender()
    {
        using IHost host = await StartAsync(endpoints =>
            endpoints
                .MapPost("/restock", (Restock _) => Results.NoContent())
                .WithRequestExample(new Untyped("restock"))
                .WithName("Restock"));

        RequestExampleRule
            .Offenders(Endpoints(host), host.Services)
            .ShouldHaveSingleItem()
            .ShouldBe("Restock binds a Restock and its example is a Untyped");
    }

    [Fact]
    public async Task An_example_its_validator_refuses_is_an_offender()
    {
        using IHost host = await StartAsync(endpoints =>
            endpoints
                .MapPost("/restock", (Restock _) => Results.NoContent())
                .WithRequestExample(Valid with { Quantity = 0 })
                .WithName("Restock"));

        RequestExampleRule
            .Offenders(Endpoints(host), host.Services)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Restock's example fails its validator at Quantity");
    }

    [Fact]
    public async Task An_example_that_holds_is_no_offender_and_a_bodiless_endpoint_is_not_asked_for_one()
    {
        using IHost host = await StartAsync(endpoints =>
        {
            endpoints.MapPost("/restock", (Restock _) => Results.NoContent()).WithRequestExample(Valid);
            endpoints.MapPost("/release/{id:guid}", (Guid _) => Results.NoContent());
        });

        RequestExampleRule.Bodied(Endpoints(host)).ShouldHaveSingleItem();
        RequestExampleRule.Offenders(Endpoints(host), host.Services).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_endpoint_refusing_its_own_example_or_answering_it_before_a_validator_is_named()
    {
        using IHost host = await StartAsync(endpoints =>
        {
            endpoints
                .MapPost("/refused", (Restock _) => Results.BadRequest())
                .WithRequestExample(Valid)
                .WithName("Refused");
            endpoints
                .MapPost("/unvalidated", (Restock _) => Results.StatusCode(503))
                .WithRequestExample(Valid)
                .WithName("Unvalidated");
            endpoints
                .MapPost(
                    "/unreachable/{id:guid}",
                    async (Guid id, Restock restock, HttpContext context) =>
                    {
                        await ValidateAsync(restock, context);
                        return Results.StatusCode(503);
                    })
                .WithRequestExample(Valid)
                .WithName("Unreachable");
        });

        IReadOnlyList<string> refused = await RefusedAsync(host, TimeSpan.FromSeconds(5));

        // The third route takes a parameter, so this also shows the path is filled rather than left to 404.
        refused.ShouldBe(
        [
            "Refused refuses its own example with 400",
            "Unvalidated answers its own example with 503 and never reaches a validator"
        ]);
    }

    [Fact]
    public async Task An_endpoint_giving_its_own_example_no_answer_before_a_validator_is_named()
    {
        using IHost host = await StartAsync(endpoints =>
        {
            endpoints
                .MapPost(
                    "/hangs",
                    async (Restock _, CancellationToken ct) =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        return Results.NoContent();
                    })
                .WithRequestExample(Valid)
                .WithName("Hangs");
            endpoints
                .MapPost(
                    "/waits",
                    async (Restock restock, HttpContext context, CancellationToken ct) =>
                    {
                        await ValidateAsync(restock, context);
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        return Results.NoContent();
                    })
                .WithRequestExample(Valid)
                .WithName("Waits");
        });

        // A decimal-comma culture, so the sentence is shown to be the same wherever the suite runs.
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        IReadOnlyList<string> refused;

        try
        {
            refused = await RefusedAsync(host, TimeSpan.FromSeconds(0.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        // A wait in front of the validators looks like one behind them, so only the probe tells them apart.
        refused.ShouldBe(["Hangs gives its own example no answer within 0.5s and never reaches a validator"]);
    }

    [Fact]
    public async Task A_host_with_no_probe_is_refused_rather_than_read_as_clean()
    {
        using IHost host = await StartAsync(
            endpoints => endpoints.MapPost("/restock", (Restock _) => Results.NoContent()).WithRequestExample(Valid),
            observe: false);

        InvalidOperationException refused = await Should.ThrowAsync<InvalidOperationException>(
            RefusedAsync(host, TimeSpan.FromSeconds(5)));

        refused.Message.ShouldBe("RefusedAsync reads a probe: register ObserveValidation on the host");
    }

    // What ValidationBehavior does for a command, so the probe sees the request arrive.
    private static async Task ValidateAsync(Restock restock, HttpContext context)
    {
        foreach (IValidator<Restock> validator in context.RequestServices.GetServices<IValidator<Restock>>())
            await validator.ValidateAsync(new ValidationContext<Restock>(restock), context.RequestAborted);
    }

    private static async Task<IReadOnlyList<string>> RefusedAsync(IHost host, TimeSpan budget)
    {
        using HttpClient client = host.GetTestClient();

        return await RequestExampleRule.RefusedAsync(
            host.Services,
            client,
            _ => { },
            budget,
            TestContext.Current.CancellationToken);
    }

    private static IEnumerable<Endpoint> Endpoints(IHost host) =>
        host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    private static async Task<JsonDocument> DocumentAsync(IHost host)
    {
        using HttpClient client = host.GetTestClient();

        string document = await client.GetStringAsync(
            new Uri("/openapi/v1.json", UriKind.Relative),
            TestContext.Current.CancellationToken);

        return JsonDocument.Parse(document);
    }

    private static Task<IHost> StartAsync(Action<IEndpointRouteBuilder> map, bool observe = true) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services
                        .AddRouting()
                        .AddCommonOpenApi()
                        .AddScoped<IValidator<Restock>, RestockValidator>();

                    if (observe)
                        RequestExampleRule.ObserveValidation(services);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapOpenApi();
                        map(endpoints);
                    });
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);
}
