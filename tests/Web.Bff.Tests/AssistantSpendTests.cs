using Common.Assistant.Protocol;
using Shouldly;
using Web.Bff.Assistant;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-204's daily budgets: per customer, for the host, and forgotten when the UTC day turns.</summary>
public sealed class AssistantSpendTests
{
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 11, 12, 23, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Every_token_kind_counts_and_reaching_the_budget_refuses()
    {
        using AssistantSpend spend = new(perCustomer: 100, perHost: 1_000, _clock);

        spend.Add("a", new Usage(50, 20, 29));
        spend.Check("a").ShouldBe(BudgetSpent.None);
        spend.Add("a", new Usage(0, 1, 0));

        spend.Check("a").ShouldBe(BudgetSpent.Customer);
        spend.Check("b").ShouldBe(BudgetSpent.None);
    }

    [Fact]
    public void The_host_budget_is_every_customer_s_together_and_wins_over_the_customer_s()
    {
        using AssistantSpend spend = new(perCustomer: 100, perHost: 150, _clock);

        spend.Add("a", new Usage(100, 0, 0));
        spend.Add("b", new Usage(50, 0, 0));

        spend.Check("a").ShouldBe(BudgetSpent.Host);
        spend.Check("c").ShouldBe(BudgetSpent.Host);
    }

    [Fact]
    public void A_new_utc_day_forgets_every_count()
    {
        using AssistantSpend spend = new(perCustomer: 100, perHost: 100, _clock);
        spend.Add("a", new Usage(100, 0, 0));

        _clock.Now = _clock.Now.AddMinutes(59);
        spend.Check("a").ShouldBe(BudgetSpent.Host, "still the same UTC day");
        _clock.Now = _clock.Now.AddMinutes(1);

        spend.Check("a").ShouldBe(BudgetSpent.None);
    }

    [Fact]
    public void The_sweep_forgets_a_past_day_s_customers_with_no_start_to_read_them()
    {
        using AssistantSpend spend = new(perCustomer: 100, perHost: 1_000, _clock);
        spend.Add("a", new Usage(1, 0, 0));

        _clock.Sweep!();
        spend.Held.ShouldBe(1, "still the same UTC day");
        _clock.Now = _clock.Now.AddHours(1);
        _clock.Sweep!();

        spend.Held.ShouldBe(0);
        _clock.Period.ShouldBe(AssistantSpend.SweepPeriod);
    }

    // Holds the timer's callback for the test to fire, so no real hour passes.
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public Action? Sweep { get; private set; }

        public TimeSpan Period { get; private set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Sweep = () => callback(state);
            Period = period;

            return base.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
