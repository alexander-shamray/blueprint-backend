using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

public class DispatcherTests
{
    [Fact]
    public async Task A_command_reaches_its_handler()
    {
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        result.ShouldBe("pong:hi");
    }

    [Fact]
    public async Task A_query_reaches_its_handler()
    {
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.QueryAsync(new Ask("why"), TestContext.Current.CancellationToken);

        result.ShouldBe("answer:why");
    }

    [Fact]
    public async Task A_command_with_no_handler_throws_rather_than_returning_nothing()
    {
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(new Unhandled(), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(nameof(Unhandled));
    }

    [Fact]
    public void The_dispatcher_cannot_be_resolved_from_the_root_provider()
    {
        // Handlers are scoped, so the dispatcher has to be (§6.2).
        using ServiceProvider provider = TestContainer.Build();

        Should.Throw<InvalidOperationException>(() =>
        {
            provider.GetRequiredService<IDispatcher>();
        });
    }

    [Fact]
    public async Task The_cached_invoker_resolves_the_handler_from_the_calling_scope()
    {
        using ServiceProvider provider = TestContainer.Build();

        Guid first;
        Guid second;

        using (IServiceScope scope = provider.CreateScope())
        {
            IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            first = await dispatcher.SendAsync(new WhichScope(), TestContext.Current.CancellationToken);
        }

        using (IServiceScope scope = provider.CreateScope())
        {
            IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            second = await dispatcher.SendAsync(new WhichScope(), TestContext.Current.CancellationToken);
        }

        first.ShouldNotBe(second);
    }

    [Fact]
    public async Task One_request_type_under_two_result_types_reaches_both_handlers()
    {
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var request = new TwoResults();

        string text = await dispatcher.SendAsync(
            (ICommand<string>)request,
            TestContext.Current.CancellationToken);
        int number = await dispatcher.SendAsync(
            (ICommand<int>)request,
            TestContext.Current.CancellationToken);

        text.ShouldBe("text");
        number.ShouldBe(42);
    }

    [Fact]
    public async Task A_request_that_is_both_a_command_and_a_query_reaches_the_right_handler_each_way()
    {
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var request = new BothWays();

        string asCommand = await dispatcher.SendAsync(request, TestContext.Current.CancellationToken);
        string asQuery = await dispatcher.QueryAsync(request, TestContext.Current.CancellationToken);

        asCommand.ShouldBe("command");
        asQuery.ShouldBe("query");
    }

    [Fact]
    public async Task The_same_request_type_dispatches_the_same_way_twice()
    {
        // A cache that hands back the wrong entry fails only on the second call.
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string first = await dispatcher.SendAsync(new Ping("one"), TestContext.Current.CancellationToken);
        string second = await dispatcher.SendAsync(new Ping("two"), TestContext.Current.CancellationToken);

        first.ShouldBe("pong:one");
        second.ShouldBe("pong:two");
    }
}
