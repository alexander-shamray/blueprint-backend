using Common.Infrastructure.Migration;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§7.4's command timeout, for the migrator §4.2 keeps from referencing Common.Infrastructure.</summary>
public static class MigratorCommand
{
    public static int TimeoutSeconds => MigratorCommandTimeout.Seconds;
}
