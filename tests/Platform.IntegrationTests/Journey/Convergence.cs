using Xunit;

namespace Platform.IntegrationTests.Journey;

/// <summary>An assertion that holds eventually, with the deadline it is held to (§12.1).</summary>
internal static class Convergence
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// <summary>Polls until <paramref name="predicate"/> holds, or throws naming <paramref name="what"/>.</summary>
    public static async Task UntilAsync(Func<Task<bool>> predicate, TimeSpan deadline, string what)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + deadline;

        while (true)
        {
            if (await predicate())
                return;

            if (DateTimeOffset.UtcNow >= until)
                throw new TimeoutException($"{what} did not hold within {deadline}.");

            await Task.Delay(Poll, TestContext.Current.CancellationToken);
        }
    }
}
