using Common.Application;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Switching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>A worker loop checks its switch on each tick and skips the pass while it is off (ADR-113).</summary>
public class SwitchedWorkerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    /// <summary>Long enough for many ticks, so a loop that ignored its switch has run by then.</summary>
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(1);

    private static ServiceProvider Build(List<string> statements, params string[] off)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new InboxTable("probe"));
        services.AddSingleton(new RetentionPolicy { Interval = Tick });
        services.AddOptions<OperatorSwitchOptions>().Configure(o => o.Off = [.. off]);
        services.AddSingleton<OperatorSwitches>();
        services.AddSingleton<RetentionPurgeService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RetentionPurgeService>());

        IDbConnectionFactory factory = Substitute.For<IDbConnectionFactory>();
        factory.Create().Returns(_ => new RecordingDbConnection(statements));
        services.AddSingleton(factory);

        return services.BuildServiceProvider();
    }

    private static async Task<int> StatementsAfterRunning(ServiceProvider provider, List<string> statements)
    {
        RetentionPurgeService worker = provider.GetRequiredService<RetentionPurgeService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(Observation, TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        return statements.Count;
    }

    [Fact]
    public async Task A_loop_whose_switch_is_off_runs_no_pass()
    {
        List<string> statements = [];
        await using ServiceProvider provider = Build(statements, RetentionPurgeService.Name);

        (await StatementsAfterRunning(provider, statements)).ShouldBe(0);
    }

    [Fact]
    public async Task The_same_loop_runs_passes_when_its_switch_is_not_listed()
    {
        // The control: without it the test above passes for a loop that never ticks.
        List<string> statements = [];
        await using ServiceProvider provider = Build(statements, "some-other-worker");

        (await StatementsAfterRunning(provider, statements)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_switch_for_a_worker_the_host_runs_is_declared_and_one_for_a_typo_is_not()
    {
        List<string> statements = [];
        await using ServiceProvider provider = Build(statements, RetentionPurgeService.Name, "retention-pruge");

        provider.GetRequiredService<OperatorSwitches>().Undeclared.ShouldBe(["retention-pruge"]);
    }
}
