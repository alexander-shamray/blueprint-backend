using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Common.Infrastructure.Switching;

/// <summary>Answers whether a receive endpoint or a worker loop is switched off, and what could be switched.</summary>
/// <remarks>A name nobody declared is a typo, and an operator needs it reported rather than ignored
/// (ADR-113).</remarks>
public sealed class OperatorSwitches(IOptionsMonitor<OperatorSwitchOptions> options, IServiceProvider services)
{
    private readonly ConcurrentDictionary<string, byte> _endpoints = new(StringComparer.Ordinal);

    /// <summary>The receive endpoints the bus has configured, which a host test compares with its
    /// declaration.</summary>
    public IReadOnlyCollection<string> Endpoints => [.. _endpoints.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Every endpoint the bus has configured and every worker the host runs.</summary>
    /// <remarks>Workers are read from the host when asked, as one resolving this class cannot be built first
    /// (ADR-113).</remarks>
    public IReadOnlyCollection<string> Declared =>
    [
        .. _endpoints.Keys
            .Concat(services.GetServices<IHostedService>().OfType<ISwitchedWorker>().Select(w => w.SwitchName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    public IReadOnlyCollection<string> Off => [.. options.CurrentValue.Off.Order(StringComparer.Ordinal)];

    /// <summary>The names switched off that no endpoint or worker declares.</summary>
    public IReadOnlyCollection<string> Undeclared
    {
        get
        {
            IReadOnlyCollection<string> declared = Declared;
            return [.. Off.Where(name => !declared.Contains(name))];
        }
    }

    /// <summary>Why the switches cannot be read as written, or null: a scalar <c>Off</c> binds as none.</summary>
    public string? Unreadable => services.GetService<OperatorSwitchSource>()?.Scalar();

    public void DeclareEndpoint(string queue) => _endpoints.TryAdd(queue, 0);

    public bool IsOff(string name) => options.CurrentValue.Off.Contains(name, StringComparer.Ordinal);

    public IDisposable? OnChange(Action listener) => options.OnChange(_ => listener());
}
