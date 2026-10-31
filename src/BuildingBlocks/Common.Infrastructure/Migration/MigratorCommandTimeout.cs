namespace Common.Infrastructure.Migration;

/// <summary>How long one statement of a migration may run, sized for a rewrite of a whole retained table.</summary>
/// <remarks>§7.4 owns the number's argument and the alert window it stays inside.</remarks>
public static class MigratorCommandTimeout
{
    public const int Seconds = 240;
}
