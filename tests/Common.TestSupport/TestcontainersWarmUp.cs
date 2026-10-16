using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    // Testcontainers 4.6.0's image-reference pattern, copied because its type is private; matched first, outside
    // that timeout, so the engine's one-off cost is already paid.
    private const string ImageReferencePattern =
        @"^(?<remote_name>(?:(?:(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9])"
        + @"(?:\.(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9]))*|\[(?:[a-fA-F0-9:]+)\])(?::[0-9]+)?\/)?"
        + @"[a-z0-9]+(?:(?:[._]|__|[-]+)[a-z0-9]+)*(?:\/[a-z0-9]+(?:(?:[._]|__|[-]+)[a-z0-9]+)*)*)"
        + @"(?::(?<tag>[\w][\w.-]{0,127}))?(?:@(?<digest>[A-Za-z][A-Za-z0-9]*(?:[-_+.][A-Za-z][A-Za-z0-9]*)*"
        + @"[:][0-9A-Fa-f]{32,}))?$";

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

    /// <summary>Matches an image reference once, so the engine's first-use cost is not spent inside Testcontainers' timeout.</summary>
    /// <returns>The tag it matched, which the test reads to show the pattern still accepts a reference.</returns>
    public static string WarmRegexEngine() =>
        new Regex(ImageReferencePattern, RegexOptions.None, TimeSpan.FromSeconds(30))
            .Match("testcontainers/ryuk:0.11.0")
            .Groups["tag"].Value;

    private static void RunInitialisers()
    {
        WarmRegexEngine();
        RuntimeHelpers.RunClassConstructor(typeof(ResourceReaper).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(TestcontainersSettings).TypeHandle);
    }
}
