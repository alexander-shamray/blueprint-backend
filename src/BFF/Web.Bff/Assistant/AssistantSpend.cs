using Common.Assistant.Protocol;

namespace Web.Bff.Assistant;

/// <summary>Which daily budget is spent, if either: the customer's own, or the host's for every customer.</summary>
public enum BudgetSpent
{
    None,
    Customer,
    Host
}

/// <summary>
/// The tokens the assistant's turns spent in the current UTC day, per customer and for the host, in memory as
/// ADR-153's single replica allows: each turn is added as it ends, so turns running when a budget is reached finish
/// past it (ADR-204).
/// </summary>
public sealed class AssistantSpend : IDisposable
{
    /// <summary>How often a timer forgets a past day's counts, so an idle replica holds no id past it.</summary>
    public static readonly TimeSpan SweepPeriod = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();

    private readonly Dictionary<string, long> _byCustomer = new(StringComparer.Ordinal);

    private readonly long _perCustomer;

    private readonly long _perHost;

    private readonly TimeProvider _time;

    private readonly ITimer _sweep;

    private DateOnly _day;

    private long _host;

    public AssistantSpend(long perCustomer, long perHost, TimeProvider time)
    {
        _perCustomer = perCustomer;
        _perHost = perHost;
        _time = time;
        _sweep = time.CreateTimer(_ => Sweep(), null, SweepPeriod, SweepPeriod);
    }

    /// <summary>The customers whose counts are held, for the retention test.</summary>
    internal int Held
    {
        get
        {
            lock (_gate)
            {
                return _byCustomer.Count;
            }
        }
    }

    /// <summary>The budget that refuses this customer's next start, or <see cref="BudgetSpent.None"/>.</summary>
    public BudgetSpent Check(string customer)
    {
        lock (_gate)
        {
            Roll();

            if (_host >= _perHost)
                return BudgetSpent.Host;

            return _byCustomer.GetValueOrDefault(customer) >= _perCustomer ? BudgetSpent.Customer : BudgetSpent.None;
        }
    }

    /// <summary>Adds a turn's tokens, each kind counted whole, whatever the provider's rate for a cached one.</summary>
    public void Add(string customer, Usage usage)
    {
        long tokens = usage.Input + usage.Output + usage.Cached;

        lock (_gate)
        {
            Roll();
            _byCustomer[customer] = _byCustomer.GetValueOrDefault(customer) + tokens;
            _host += tokens;
        }
    }

    /// <summary>Stops the sweep when the host's container disposes the runtime.</summary>
    public void Dispose() => _sweep.Dispose();

    private void Sweep()
    {
        lock (_gate)
        {
            Roll();
        }
    }

    // A new day forgets every count, which is also what bounds the map: it holds today's customers alone.
    private void Roll()
    {
        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

        if (today == _day)
            return;

        _day = today;
        _host = 0;
        _byCustomer.Clear();
    }
}
