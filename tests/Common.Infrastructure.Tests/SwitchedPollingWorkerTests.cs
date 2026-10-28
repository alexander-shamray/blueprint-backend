using Common.Infrastructure.Switching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>The loop the switched workers share: an off tick runs no pass, a failed pass ticks again
/// (ADR-113).</summary>
public class SwitchedPollingWorkerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Observation = TimeSpan.FromMilliseconds(400);

    private sealed class Probe(
        OperatorSwitches? switches, bool passAtStart = false, TimeSpan? drain = null, TimeSpan? interval = null)
        : SwitchedPollingWorker(switches)
    {
        private readonly TaskCompletionSource _firstPass = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _passes;
        private int _failures;

        public int Passes => Volatile.Read(ref _passes);

        public int Failures => Volatile.Read(ref _failures);

        public Task FirstPass => _firstPass.Task;

        /// <summary>Completes once <see cref="ReachAt"/> passes have run.</summary>
        public Task Reached => _reached.Task;

        public int ReachAt { get; init; } = 1;

        /// <summary>How many passes, from the first, report work still waiting.</summary>
        public int FilledPasses { get; init; }

        public Action? AfterPass { get; init; }

        public bool Throws { get; init; }

        public bool BlockUntilDrained { get; init; }

        public bool AwaitsStop { get; init; }

        public bool CutByStop { get; private set; }

        public bool Drained { get; private set; }

        public CancellationToken DrainSeen { get; private set; }

        public override string SwitchName => "probe";

        protected override TimeSpan PassInterval => interval ?? Tick;

        protected override bool PassAtStart => passAtStart;

        protected override TimeSpan? StopDrain => drain;

        protected override bool PassFilled => Passes <= FilledPasses;

        protected override async Task RunPassAsync(CancellationToken stoppingToken)
        {
            int passes = Interlocked.Increment(ref _passes);
            DrainSeen = Drain;
            _firstPass.TrySetResult();
            AfterPass?.Invoke();

            if (passes >= ReachAt)
                _reached.TrySetResult();

            if (BlockUntilDrained)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), Drain);
                }
                catch (OperationCanceledException)
                {
                    Drained = true;
                }
            }

            if (AwaitsStop)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
                }
                finally
                {
                    CutByStop = stoppingToken.IsCancellationRequested;
                }
            }

            if (Throws)
                throw new InvalidOperationException("the pass failed");
        }

        protected override void OnPassFailed(Exception exception) => Interlocked.Increment(ref _failures);
    }

    private static OperatorSwitches SwitchesWith(params string[] off) => SwitchesOver([.. off]);

    // The options hold this list itself, so a test that adds to it switches the worker off mid-run.
    private static OperatorSwitches SwitchesOver(List<string> off)
    {
        ServiceCollection services = new();
        services.AddOptions<OperatorSwitchOptions>().Configure(o => o.Off = off);
        ServiceProvider provider = services.BuildServiceProvider();

        return new OperatorSwitches(provider.GetRequiredService<IOptionsMonitor<OperatorSwitchOptions>>(), provider);
    }

    private static async Task RunAsync(Probe worker)
    {
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(Observation, TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_tick_with_its_switch_off_runs_no_pass()
    {
        Probe worker = new(SwitchesWith("probe"));

        await RunAsync(worker);

        worker.Passes.ShouldBe(0);
    }

    [Fact]
    public async Task A_tick_with_another_switch_off_runs_passes()
    {
        // The control: without it the test above passes for a loop that never ticks.
        Probe worker = new(SwitchesWith("some-other-worker"));

        await RunAsync(worker);

        worker.Passes.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task A_host_with_no_switches_runs_passes()
    {
        Probe worker = new(null);

        await RunAsync(worker);

        worker.Passes.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task A_pass_that_throws_is_reported_and_the_next_tick_runs_another()
    {
        Probe worker = new(null) { Throws = true };

        await RunAsync(worker);

        // One pass may be cut by the stop between its start and its report, and a stop's failure is not one.
        worker.Failures.ShouldBeGreaterThan(1);
        worker.Failures.ShouldBeGreaterThanOrEqualTo(worker.Passes - 1);
    }

    [Fact]
    public async Task A_pass_at_start_runs_before_the_first_tick()
    {
        // The tick outlasts the wait, so a pass seen at all did not wait for the timer. Awaiting the pass, not a fixed
        // window, keeps the thread pool's delay in starting the loop out of what is measured.
        Probe worker = new(null, passAtStart: true, interval: TimeSpan.FromHours(1));

        await worker.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await worker.FirstPass.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        worker.Passes.ShouldBe(1);
    }

    [Fact]
    public async Task A_pass_at_start_still_honours_a_switch_that_is_off()
    {
        Probe worker = new(SwitchesWith("probe"), passAtStart: true);

        await RunAsync(worker);

        worker.Passes.ShouldBe(0);
    }

    [Fact]
    public async Task A_filled_pass_runs_the_next_at_once_rather_than_at_the_tick()
    {
        // The tick outlasts the test, so the third pass ran only because the two before it filled (ADR-163).
        Probe worker = new(null, passAtStart: true, interval: TimeSpan.FromHours(1)) { FilledPasses = 2, ReachAt = 3 };

        await worker.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await worker.Reached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        worker.Passes.ShouldBe(3);
    }

    [Fact]
    public async Task A_filled_pass_still_reads_the_switch_before_the_next()
    {
        // The first pass switches its own worker off and fills, so a second pass is one that skipped the switch.
        List<string> off = [];
        Probe worker = new(SwitchesOver(off), passAtStart: true, interval: TimeSpan.FromHours(1))
        {
            FilledPasses = int.MaxValue,
            AfterPass = () => off.Add("probe")
        };

        await worker.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await worker.Reached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await Task.Delay(Observation, TestContext.Current.CancellationToken);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        worker.Passes.ShouldBe(1);
    }

    [Fact]
    public async Task A_stop_ends_a_run_of_filled_passes()
    {
        // Every pass fills, so only the stop can end the loop; bounded, so a loop that ignores it fails here.
        Probe worker = new(null, passAtStart: true, interval: TimeSpan.FromHours(1)) { FilledPasses = int.MaxValue };

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.FirstPass.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // StopAsync waits on its own token for a loop that never ends, so that token carries the bound.
        using CancellationTokenSource bound =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        await worker.StopAsync(bound.Token);

        worker.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task A_stop_returns_without_a_pass_failure_being_reported()
    {
        // The pass is under way at the stop and throws its cancellation, so only the filter keeps it unreported.
        Probe worker = new(null, passAtStart: true, interval: TimeSpan.FromHours(1)) { AwaitsStop = true };

        await worker.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await worker.FirstPass.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        worker.CutByStop.ShouldBeTrue();
        worker.Failures.ShouldBe(0);
    }

    [Fact]
    public async Task Without_a_drain_a_stop_cancels_the_rows_token_with_it()
    {
        Probe worker = new(null);

        await RunAsync(worker);

        worker.DrainSeen.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task With_a_drain_a_stop_leaves_the_rows_token_live_until_its_budget_has_run()
    {
        Probe worker = new(null, drain: TimeSpan.FromSeconds(30));

        await RunAsync(worker);

        worker.DrainSeen.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task A_pass_under_way_at_a_stop_has_its_rows_token_cancelled_when_the_budget_has_run()
    {
        // The pass blocks until the rows token fires, so only the drain can end it and let the stop return.
        Probe worker = new(null, drain: TimeSpan.FromMilliseconds(50)) { BlockUntilDrained = true };

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(Observation, TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        worker.Drained.ShouldBeTrue();
    }
}
