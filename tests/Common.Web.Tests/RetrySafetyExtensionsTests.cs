using Common.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What §8.5's two declarations leave on an endpoint (ADR-058).</summary>
public class RetrySafetyExtensionsTests
{
    private sealed record Keyed(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.keyed";
    }

    private sealed class Recording : IEndpointConventionBuilder
    {
        public List<Action<EndpointBuilder>> Conventions { get; } = [];

        public void Add(Action<EndpointBuilder> convention) => Conventions.Add(convention);
    }

    [Fact]
    public void RetrySafe_leaves_its_kind_on_the_endpoint()
    {
        using WebApplication app = WebApplication.CreateSlimBuilder().Build();

        app.MapPut("/stock/{id:guid}", (Guid id) => Results.NoContent()).RetrySafe(RetrySafety.Convergent);

        Endpoint endpoint = Endpoints(app).ShouldHaveSingleItem();

        endpoint.Metadata
            .GetOrderedMetadata<RetrySafetyMetadata>()
            .ShouldBe([new RetrySafetyMetadata(RetrySafety.Convergent)]);
    }

    [Fact]
    public void RetrySafe_on_a_group_reaches_every_endpoint_mapped_in_it()
    {
        using WebApplication app = WebApplication.CreateSlimBuilder().Build();

        RouteGroupBuilder group = app.MapGroup("/v1").RetrySafe(RetrySafety.ReadOnly);
        group.MapPost("/quote", () => Results.Ok());
        group.MapPost("/estimate", () => Results.Ok());

        IReadOnlyList<Endpoint> endpoints = Endpoints(app);

        endpoints.Count.ShouldBe(2);
        foreach (Endpoint endpoint in endpoints)
        {
            endpoint.Metadata
                .GetOrderedMetadata<RetrySafetyMetadata>()
                .ShouldBe([new RetrySafetyMetadata(RetrySafety.ReadOnly)], endpoint.DisplayName);
        }
    }

    [Fact]
    public void RetrySafe_takes_any_convention_builder_and_hands_it_back()
    {
        // MapGrpcService's builder is not a RouteHandlerBuilder, and a gRPC service is declared on it (§9.7).
        Recording builder = new();

        Recording returned = builder.RetrySafe(RetrySafety.ReadOnly);

        returned.ShouldBeSameAs(builder);

        RouteEndpointBuilder endpoint = new(_ => Task.CompletedTask, RoutePatternFactory.Parse("/probe"), order: 0);
        foreach (Action<EndpointBuilder> convention in builder.Conventions)
            convention(endpoint);

        endpoint.Metadata.OfType<RetrySafetyMetadata>().ShouldBe([new RetrySafetyMetadata(RetrySafety.ReadOnly)]);
    }

    [Fact]
    public void Idempotent_names_the_command_the_handler_builds()
    {
        using WebApplication app = WebApplication.CreateSlimBuilder().Build();

        app.MapPost("/reservations/{id:guid}/reinstate", (Guid id) => Results.NoContent()).Idempotent<Keyed>();

        Endpoint endpoint = Endpoints(app).ShouldHaveSingleItem();

        endpoint.Metadata
            .GetOrderedMetadata<IdempotentCommandMetadata>()
            .ShouldBe([new IdempotentCommandMetadata(typeof(Keyed))]);
    }

    private static IReadOnlyList<Endpoint> Endpoints(IEndpointRouteBuilder app) =>
        [.. app.DataSources.SelectMany(source => source.Endpoints)];
}
