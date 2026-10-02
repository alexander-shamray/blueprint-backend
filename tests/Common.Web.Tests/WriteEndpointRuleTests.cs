using Common.Application;
using Common.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What <see cref="WriteEndpointRule"/> selects and names, over endpoints mapped for the purpose.</summary>
public class WriteEndpointRuleTests
{
    public sealed record Reached(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.reached";
    }

    public sealed record Built(Guid CommandId, Guid Id) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.built";
    }

    public sealed record Unreached(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.unreached";
    }

    public readonly record struct Counted(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.counted";
    }

    public sealed record Wrapper(Guid Id, [FromBody] Reached Command);

    [Fact]
    public void A_table_that_keeps_the_rule_has_no_offender()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
        {
            app.MapPost("/keyed", (Reached command) => Results.NoContent()).RequireAuthorization().WithName("Keyed");
            app
                .MapPut("/set/{id:guid}", (Guid id) => Results.NoContent())
                .RetrySafe(RetrySafety.Convergent)
                .WithName("Set");
            app.MapGet("/read", () => Results.Ok()).WithName("Read");
        });

        WriteEndpointRule.Offenders(endpoints).ShouldBeEmpty();
        Names(WriteEndpointRule.Writes(endpoints)).ShouldBe(["Keyed", "Set"]);
    }

    [Fact]
    public void An_undeclared_write_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app
                .MapPost("/cancel/{id:guid}", (Guid id) => Results.NoContent())
                .RequireAuthorization()
                .WithName("Cancel"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Cancel accepts a write and is neither keyed nor declared retry-safe");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Every_write_verb_is_a_write(string verb)
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapMethods("/thing/{id:guid}", [verb], (Guid id) => Results.NoContent()).WithName("Thing"));

        Names(WriteEndpointRule.Writes(endpoints)).ShouldBe(["Thing"]);
        WriteEndpointRule.Offenders(endpoints).ShouldHaveSingleItem().ShouldStartWith("Thing accepts a write");
    }

    [Fact]
    public void A_write_that_is_both_keyed_and_declared_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app
                .MapPost("/keyed", (Reached command) => Results.NoContent())
                .RequireAuthorization()
                .RetrySafe(RetrySafety.Convergent)
                .WithName("Keyed"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Keyed is keyed by Reached and declared Convergent");
    }

    [Fact]
    public void Every_command_an_endpoint_is_keyed_by_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app
                .MapPost("/keyed", (Reached command) => Results.NoContent())
                .Idempotent<Built>()
                .RetrySafe(RetrySafety.Convergent)
                .WithName("Keyed"));

        IReadOnlyList<string> offenders = WriteEndpointRule.Offenders(endpoints);

        offenders.Count.ShouldBe(2);
        offenders[0].ShouldStartWith("Keyed is keyed by Reached and Built and requires no authorisation");
        offenders[1].ShouldStartWith("Keyed is keyed by Reached and Built and declared Convergent");
    }

    [Fact]
    public void A_declaration_on_a_group_makes_a_keyed_endpoint_inside_it_an_offender()
    {
        // The group's metadata reaches every endpoint mapped in it, a later one included.
        IReadOnlyList<Endpoint> endpoints = Map(app =>
        {
            RouteGroupBuilder group = app.MapGroup("/v1").RequireAuthorization().RetrySafe(RetrySafety.ReadOnly);
            group.MapPost("/quote", () => Results.Ok()).WithName("Quote");
            group.MapPost("/keyed", (Reached command) => Results.NoContent()).WithName("Keyed");
        });

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Keyed is keyed by Reached and declared ReadOnly");
    }

    [Fact]
    public void A_write_declared_with_two_kinds_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
        {
            RouteGroupBuilder group = app.MapGroup("/v1").RetrySafe(RetrySafety.ReadOnly);
            group
                .MapPut("/set/{id:guid}", (Guid id) => Results.NoContent())
                .RetrySafe(RetrySafety.Convergent)
                .WithName("Set");
        });

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Set declares ReadOnly and Convergent");
    }

    [Fact]
    public void A_keyed_endpoint_that_allows_anonymous_callers_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app
                .MapGroup("/v1")
                .RequireAuthorization()
                .MapPost("/keyed", (Reached command) => Results.NoContent())
                .AllowAnonymous()
                .WithName("Keyed"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Keyed is keyed by Reached and allows anonymous callers");
    }

    [Fact]
    public void A_keyed_endpoint_with_no_authorisation_data_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapPost("/keyed", (Reached command) => Results.NoContent()).WithName("Keyed"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Keyed is keyed by Reached and requires no authorisation");
    }

    [Fact]
    public void An_endpoint_that_builds_its_command_is_keyed_by_saying_so()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app
                .MapPost("/built/{id:guid}", (Guid id) => Results.NoContent())
                .RequireAuthorization()
                .Idempotent<Built>()
                .WithName("Built"));

        WriteEndpointRule.Offenders(endpoints).ShouldBeEmpty();
    }

    [Fact]
    public void The_subject_rule_reaches_an_endpoint_keyed_by_declaration()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapPost("/built/{id:guid}", (Guid id) => Results.NoContent()).Idempotent<Built>().WithName("Built"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Built is keyed by Built and requires no authorisation");
    }

    [Fact]
    public void A_command_bound_inside_a_parameter_object_is_not_seen_until_the_endpoint_says_so()
    {
        // The selector reads the handler's own parameters, so a command one level down fails closed.
        IReadOnlyList<Endpoint> hidden = Map(app =>
            app
                .MapPost("/wrapped/{id:guid}", ([AsParameters] Wrapper request) => Results.NoContent())
                .RequireAuthorization()
                .WithName("Wrapped"));

        WriteEndpointRule
            .Offenders(hidden)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Wrapped accepts a write and is neither keyed nor declared retry-safe");

        IReadOnlyList<Endpoint> declared = Map(app =>
            app
                .MapPost("/wrapped/{id:guid}", ([AsParameters] Wrapper request) => Results.NoContent())
                .RequireAuthorization()
                .Idempotent<Reached>()
                .WithName("Wrapped"));

        WriteEndpointRule.Offenders(declared).ShouldBeEmpty();
    }

    [Fact]
    public void A_get_is_ignored()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app => app.MapGet("/read", () => Results.Ok()).WithName("Read"));

        WriteEndpointRule.Writes(endpoints).ShouldBeEmpty();
        WriteEndpointRule.Offenders(endpoints).ShouldBeEmpty();
    }

    [Fact]
    public void A_route_handler_mapped_with_no_method_is_a_write()
    {
        // Map with a handler and no verb answers a POST as readily as a GET.
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.Map("/anything/{id:guid}", (Guid id) => Results.NoContent()).WithName("Anything"));

        endpoints.ShouldHaveSingleItem().Metadata.GetMetadata<IHttpMethodMetadata>().ShouldBeNull();
        Names(WriteEndpointRule.Writes(endpoints)).ShouldBe(["Anything"]);
        WriteEndpointRule.Unrestricted(endpoints).ShouldBeEmpty();
        WriteEndpointRule.Offenders(endpoints).ShouldHaveSingleItem().ShouldStartWith("Anything accepts a write");
    }

    [Fact]
    public void A_handler_mapped_for_a_read_and_a_write_is_a_write()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapMethods("/both", ["GET", "POST"], () => Results.Ok()).WithName("Both"));

        Names(WriteEndpointRule.Writes(endpoints)).ShouldBe(["Both"]);
    }

    [Fact]
    public void A_grpc_method_is_a_write_and_is_named_until_its_service_is_declared()
    {
        // A gRPC method as routing holds it: a POST with no handler MethodInfo, named by its display name alone.
        Endpoint undeclared = Bare("gRPC - /probe.v1.Probe/Get", new HttpMethodMetadata(["POST"]));
        Endpoint declared = Bare(
            "gRPC - /probe.v1.Probe/Get",
            new HttpMethodMetadata(["POST"]),
            new RetrySafetyMetadata(RetrySafety.ReadOnly));

        WriteEndpointRule.Writes([undeclared]).ShouldHaveSingleItem();
        WriteEndpointRule
            .Offenders([undeclared])
            .ShouldHaveSingleItem()
            .ShouldStartWith("gRPC - /probe.v1.Probe/Get accepts a write and is neither keyed nor declared");

        WriteEndpointRule.Writes([declared]).ShouldHaveSingleItem();
        WriteEndpointRule.Offenders([declared]).ShouldBeEmpty();
    }

    [Fact]
    public void A_grpc_fallback_is_left_out_and_named_as_unrestricted()
    {
        // A bare RequestDelegate naming no method, which the service's own declaration reaches.
        Endpoint fallback = Bare("gRPC - Unimplemented service", new RetrySafetyMetadata(RetrySafety.ReadOnly));

        WriteEndpointRule.Writes([fallback]).ShouldBeEmpty();
        WriteEndpointRule.Offenders([fallback]).ShouldBeEmpty();
        WriteEndpointRule.Unrestricted([fallback]).ShouldBe([fallback]);
    }

    [Fact]
    public void The_probes_are_left_out_and_named_as_unrestricted()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddHealthChecks();
        using WebApplication app = builder.Build();

        app.MapCommonHealthEndpoints(ownsNoReadinessDependencies: true);

        IReadOnlyList<Endpoint> endpoints = [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)];

        WriteEndpointRule.Writes(endpoints).ShouldBeEmpty();
        WriteEndpointRule.Offenders(endpoints).ShouldBeEmpty();
        WriteEndpointRule
            .Unrestricted(endpoints)
            .Select(endpoint => endpoint.DisplayName)
            .ShouldBe(["Health checks", "Health checks", "Health checks"]);
    }

    [Fact]
    public void A_request_delegate_a_host_maps_with_no_method_is_unrestricted_and_not_a_write()
    {
        // The shape the selection cannot tell from the framework's own, which is why a host's suite names each.
        RequestDelegate raw = _ => Task.CompletedTask;
        IReadOnlyList<Endpoint> endpoints = Map(app => app.Map("/raw", raw).WithName("Raw"));

        WriteEndpointRule.Writes(endpoints).ShouldBeEmpty();
        Names(WriteEndpointRule.Unrestricted(endpoints)).ShouldBe(["Raw"]);
    }

    [Fact]
    public void A_keyed_command_no_endpoint_reaches_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
        {
            app.MapPost("/keyed", (Reached command) => Results.NoContent()).RequireAuthorization();
            app
                .MapPost("/built/{id:guid}", (Guid id) => Results.NoContent())
                .RequireAuthorization()
                .Idempotent<Built>();
        });

        IReadOnlyList<string> offenders =
            WriteEndpointRule.Offenders(endpoints, typeof(WriteEndpointRuleTests).Assembly);

        offenders.ShouldContain(offender =>
            offender.StartsWith("Unreached is an idempotent command no endpoint reaches", StringComparison.Ordinal));
        offenders.ShouldNotContain(offender => offender.StartsWith("Reached ", StringComparison.Ordinal));
        offenders.ShouldNotContain(offender => offender.StartsWith("Built ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_struct_command_is_an_idempotent_command_of_its_assembly()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapPost("/counted", (Counted command) => Results.NoContent()).RequireAuthorization());

        WriteEndpointRule
            .Offenders(endpoints, typeof(WriteEndpointRuleTests).Assembly)
            .ShouldNotContain(offender => offender.StartsWith("Counted ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scan_of_the_wrong_assembly_is_named_rather_than_empty()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapPost("/keyed", (Reached command) => Results.NoContent()).RequireAuthorization());

        WriteEndpointRule
            .Offenders(endpoints, typeof(IIdempotentCommand).Assembly)
            .ShouldHaveSingleItem()
            .ShouldBe(
                "Reached keys an endpoint and is not an idempotent command of Common.Application, " +
                "so this scan is reading the wrong assembly");
    }

    private static IReadOnlyList<Endpoint> Map(Action<IEndpointRouteBuilder> map)
    {
        using WebApplication app = WebApplication.CreateSlimBuilder().Build();

        map(app);

        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)];
    }

    private static Endpoint Bare(string displayName, params object[] metadata) =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), displayName);

    private static string?[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)];
}
