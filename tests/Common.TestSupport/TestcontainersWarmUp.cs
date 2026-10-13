using System.Runtime.CompilerServices;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Common.TestSupport;

/// <summary>Runs Testcontainers' type initialisers before a fixture builds a container, and names a failure.</summary>
/// <remarks>
/// A starved runner can time out <c>ResourceReaper</c>'s one-second tag regex, and an initialiser that threw
/// never runs again, so only a new process recovers; <see cref="DaemonRetry"/> cannot reach it.
/// </remarks>
public static class TestcontainersWarmUp
{
    public static void Ensure() => Ensure(RunInitialisers);

    /// <summary>Warms up, then builds, for a field initialiser that cannot hold a statement.</summary>
    public static T Build<T>(Func<T> build) => Build(RunInitialisers, build);

    /// <summary>Runs <paramref name="initialise"/> as <see cref="Ensure(Action)"/> does, then builds.</summary>
    public static T Build<T>(Action initialise, Func<T> build)
    {
        Ensure(initialise);
        return build();
    }

    /// <summary>Runs <paramref name="initialise"/>, rewording a failed type initialiser to name this step.</summary>
    public static void Ensure(Action initialise)
    {
        try
        {
            initialise();
        }
        catch (TypeInitializationException ex)
        {
            string cause = (ex.InnerException ?? ex).GetType().Name;
            throw new InvalidOperationException(
                $"Testcontainers' static initialisation failed ({cause}); the process cannot retry it, "
                + "so every container fixture in it fails. Run the job again.",
                ex);
        }
    }

    private static void RunInitialisers()
    {
        RuntimeHelpers.RunClassConstructor(typeof(ResourceReaper).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(TestcontainersSettings).TypeHandle);
    }
}
