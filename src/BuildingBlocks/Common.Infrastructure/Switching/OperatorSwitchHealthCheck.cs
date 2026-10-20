using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Common.Infrastructure.Switching;

/// <summary>Reports Degraded while any name is switched off, so a loop that is off shows in readiness
/// (ADR-115).</summary>
/// <remarks>Degraded answers 200 on <c>/health/ready</c> (§13.5), so the pod stays in service.</remarks>
public sealed class OperatorSwitchHealthCheck(OperatorSwitches switches) : IHealthCheck
{
    public const string Name = "operator-switches";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<string> off = switches.Off;
        HealthCheckResult result = off.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"Switched off by an operator: {string.Join(", ", off)}.");

        return Task.FromResult(result);
    }
}
