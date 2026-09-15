using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>
/// Unregistered or misordered, the pipeline fails silently and completely
/// (§6.3) — the dispatcher invokes the handler alone and the request returns
/// 200 having written nothing.
/// </summary>
public class PipelineOrderingTests
{
    [Fact]
    public async Task The_first_registered_behaviour_is_the_outermost()
    {
        using ServiceProvider provider = TestContainer.Build(services =>
        {
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FirstBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(SecondBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ThirdBehavior<,>));
        });

        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        PipelineLog log = scope.ServiceProvider.GetRequiredService<PipelineLog>();

        log.Entries.ShouldBe(
            [
                "enter FirstBehavior",
                "enter SecondBehavior",
                "enter ThirdBehavior",
                "leave ThirdBehavior",
                "leave SecondBehavior",
                "leave FirstBehavior"
            ],
            "registration order is pipeline order — the dispatcher reverses the list (§6.2)");
    }

    [Fact]
    public async Task Behaviours_run_in_the_registered_order_and_not_in_reflection_order()
    {
        // The same three behaviours, registered the other way round. Without
        // this arm the test above passes on a dispatcher that happens to run
        // them in whatever order the container returned.
        using ServiceProvider provider = TestContainer.Build(services =>
        {
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ThirdBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(SecondBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FirstBehavior<,>));
        });

        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        PipelineLog log = scope.ServiceProvider.GetRequiredService<PipelineLog>();

        log.Entries.ShouldBe(
            [
                "enter ThirdBehavior",
                "enter SecondBehavior",
                "enter FirstBehavior",
                "leave FirstBehavior",
                "leave SecondBehavior",
                "leave ThirdBehavior"
            ],
            "the order is the registration's, whatever the names suggest");
    }

    [Fact]
    public async Task A_query_runs_the_behaviours_registered_for_it()
    {
        using ServiceProvider provider = TestContainer.Build(services =>
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FirstBehavior<,>)));

        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.QueryAsync(new Ask("why"), TestContext.Current.CancellationToken);

        PipelineLog log = scope.ServiceProvider.GetRequiredService<PipelineLog>();

        log.Entries.ShouldBe(["enter FirstBehavior", "leave FirstBehavior"]);
    }

    [Fact]
    public async Task A_behaviour_constrained_to_commands_runs_for_a_command()
    {
        using ServiceProvider provider = TestContainer.Build(services =>
        {
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FirstBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(CommandOnlyBehavior<,>));
        });

        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        PipelineLog log = scope.ServiceProvider.GetRequiredService<PipelineLog>();

        log.Entries.ShouldBe(
            [
                "enter FirstBehavior",
                "enter CommandOnlyBehavior",
                "leave CommandOnlyBehavior",
                "leave FirstBehavior"
            ]);
    }

    [Fact]
    public async Task A_behaviour_constrained_to_commands_is_skipped_for_a_query()
    {
        // The skipping is a container feature, not a language one: constraints
        // on open generic registrations have been honoured since .NET 7, and on
        // an older container the same registration throws when the first query
        // resolves. §6.3 leans on it for TransactionBehavior and
        // IdempotencyBehavior, so it is asserted rather than trusted.
        using ServiceProvider provider = TestContainer.Build(services =>
        {
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FirstBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(CommandOnlyBehavior<,>));
        });

        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.QueryAsync(new Ask("why"), TestContext.Current.CancellationToken);

        PipelineLog log = scope.ServiceProvider.GetRequiredService<PipelineLog>();

        log.Entries.ShouldBe(
            [
                "enter FirstBehavior",
                "leave FirstBehavior"
            ],
            "a query opening a transaction is the defect this catches — §6.3");
    }

    [Fact]
    public async Task A_behaviour_that_never_calls_next_stops_the_handler()
    {
        using ServiceProvider provider = TestContainer.Build(services =>
        {
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ShortCircuitBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FirstBehavior<,>));
        });

        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        PipelineLog log = scope.ServiceProvider.GetRequiredService<PipelineLog>();

        result.ShouldBeNull();
        log.Entries.ShouldBe(["short-circuit"], "nothing inside the short circuit ran");
    }

    [Fact]
    public async Task With_no_behaviours_registered_the_handler_still_runs()
    {
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        result.ShouldBe("pong:hi");
        scope.ServiceProvider.GetRequiredService<PipelineLog>().Entries.ShouldBeEmpty();
    }
}
