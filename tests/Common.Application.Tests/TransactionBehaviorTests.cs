using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>§6.3's sequence, asserted through fakes that share one <see cref="PipelineLog"/>.</summary>
public class TransactionBehaviorTests
{
    private const string Key =
        "0195e4b2-0000-7000-8000-00000000000a:tests.approve:0195e4b2-0000-7000-8000-0000000000ff";

    [Fact]
    public async Task A_successful_command_enters_the_unit_dispatches_then_saves_once()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Result result = await Dispatch(scope, new Approve());

        result.IsSuccess.ShouldBeTrue();
        Log(scope).Entries.ShouldBe(
            ["execute", "dispatch", "count", "save"],
            "dispatch before save is what puts outbox rows into the same save, " +
            "and dispatch before the count is §6.3's own comment");
    }

    [Fact]
    public async Task A_failed_result_skips_dispatch_and_save()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Result result = await Dispatch(scope, new Reject());

        result.IsFailure.ShouldBeTrue();
        Log(scope).Entries.ShouldBe(
            ["execute"],
            "a rejected command must not stage events or save — §6.3's failure guard");
    }

    [Fact]
    public async Task A_command_whose_result_is_not_a_Result_commits_normally()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        result.ShouldBe("pong:hi");
        Log(scope).Entries.ShouldBe(
            ["execute", "dispatch", "count", "save"],
            "the failure guard is a pattern match on Result; any other result type commits");
    }

    [Fact]
    public async Task A_nested_dispatch_runs_inside_the_open_unit_without_a_second_one()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().HasActiveTransaction = true;

        Result result = await Dispatch(scope, new Approve());

        result.IsSuccess.ShouldBeTrue();
        Log(scope).Entries.ShouldBeEmpty(
            "already inside a transaction, the behaviour is a passthrough — " +
            "the outer dispatch owns the dispatch-count-save sequence");
    }

    [Fact]
    public async Task A_command_modifying_two_aggregates_throws_and_saves_nothing()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().AggregateCount = 2;

        InvariantViolationException exception =
            await Should.ThrowAsync<InvariantViolationException>(() => Dispatch(scope, new Approve()));

        exception.Message.ShouldContain(nameof(Approve));
        exception.Message.ShouldContain("2");

        // Two counts: the guard reads the property and the message interpolates it again.
        Log(scope).Entries.ShouldBe(
            ["execute", "dispatch", "count", "count"],
            "the guard fires after dispatch — §6.3 counts staged rows too — and before save");
    }

    [Fact]
    public async Task A_throwing_handler_saves_nothing()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(new Boom(), TestContext.Current.CancellationToken));

        Log(scope).Entries.ShouldBe(
            ["execute"],
            "an exception unwinds through the unit; nothing is dispatched or saved");
    }

    [Fact]
    public async Task A_query_never_touches_the_unit_of_work()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.QueryAsync(new Ask("why"), TestContext.Current.CancellationToken);

        result.ShouldBe("answer:why");
        Log(scope).Entries.ShouldBeEmpty(
            "a query opening a transaction is the defect this catches — §6.3");
    }

    [Fact]
    public async Task A_claimed_command_reads_the_marker_first_and_writes_it_before_the_save()
    {
        // §8.5's durable marker: read before the handler, written after the aggregate guard and before the save.
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        string key = Claim(scope);

        Result result = await Dispatch(scope, new Approve());

        result.IsSuccess.ShouldBeTrue();
        Log(scope).Entries.ShouldBe(
            ["execute", "marker-read", "dispatch", "count", "marker-write", "save"]);
        Markers(scope).Written.ShouldBe([key]);
    }

    [Fact]
    public async Task An_unclaimed_command_does_not_touch_the_marker_store_at_all()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Result result = await Dispatch(scope, new Approve());

        result.IsSuccess.ShouldBeTrue();
        Log(scope).Entries.ShouldBe(["execute", "dispatch", "count", "save"]);
        Markers(scope).Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_key_a_previous_attempt_committed_is_refused_before_the_handler_runs()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        string key = Claim(scope);
        Markers(scope).Committed.Add(key);

        CommandAlreadyCommittedException thrown =
            await Should.ThrowAsync<CommandAlreadyCommittedException>(
                () => Dispatch(scope, new Approve()));

        thrown.Key.ShouldBe(key);
        Log(scope).Entries.ShouldBe(
            ["execute", "marker-read"],
            "the handler never ran, so nothing was dispatched, counted or saved");
    }

    [Fact]
    public async Task A_refused_command_leaves_no_marker_behind()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        Claim(scope);

        Result result = await Dispatch(scope, new Reject());

        result.IsFailure.ShouldBeTrue();
        Log(scope).Entries.ShouldBe(["execute", "marker-read"]);
        Markers(scope).Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_command_refused_by_the_aggregate_guard_leaves_no_marker_behind()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        Claim(scope);
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().AggregateCount = 2;

        await Should.ThrowAsync<InvariantViolationException>(() => Dispatch(scope, new Approve()));

        Markers(scope).Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_nested_dispatch_writes_no_marker_of_its_own()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        Claim(scope);
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().HasActiveTransaction = true;

        Result result = await Dispatch(scope, new Approve());

        result.IsSuccess.ShouldBeTrue();
        Log(scope).Entries.ShouldBeEmpty();
        Markers(scope).Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_key_is_read_before_the_handler_and_never_again()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        string outer = Claim(scope);

        Result result = await scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(
                new Reclaim("someone-else:tests.inner:0195e4b2-0000-7000-8000-00000000000c"),
                TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        Markers(scope).Written.ShouldBe(
            [outer],
            "the marker records the command this transaction is committing, and the context " +
            "no longer names it by the time the handler has returned");
    }

    private static ServiceProvider BuildProvider() =>
        TestContainer.Build(services =>
        {
            services.AddScoped<FakeUnitOfWork>();
            services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<FakeUnitOfWork>());
            services.AddScoped<IDomainEventDispatcher, FakeDomainEventDispatcher>();
            services.AddScoped<RecordingMarkerStore>();
            services.AddScoped<IIdempotencyMarkerStore>(
                sp => sp.GetRequiredService<RecordingMarkerStore>());
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
        });

    /// <summary>Puts a key on the scope's context, as §8.5's behaviour does after a successful claim.</summary>
    private static string Claim(IServiceScope scope, string key = Key)
    {
        scope.ServiceProvider.GetRequiredService<IdempotencyContext>().Claim(key);
        return key;
    }

    private static RecordingMarkerStore Markers(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<RecordingMarkerStore>();

    private static Task<Result> Dispatch(IServiceScope scope, ICommand<Result> command) =>
        scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(command, TestContext.Current.CancellationToken);

    private static PipelineLog Log(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<PipelineLog>();
}
