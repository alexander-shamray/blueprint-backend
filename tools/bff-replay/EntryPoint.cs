namespace BffReplay;

/// <summary>The process: the real environment and console, and nothing the tests cannot reach without them.</summary>
/// <remarks>
/// Not top-level statements: those compile to a global <c>Program</c>, which <c>Web.Bff.Tests</c> already names as
/// the BFF's own for <c>WebApplicationFactory</c> (§12.4), and two would make it ambiguous.
/// </remarks>
internal static class EntryPoint
{
    private static Task<int> Main(string[] args) =>
        ReplayCommand.RunAsync(
            args,
            Environment.GetEnvironmentVariable,
            Console.Out,
            Console.Error,
            CancellationToken.None);
}
