namespace Common.TestSupport;

/// <summary>Starts a host that must refuse to start until it says why, not that its provider is disposed.</summary>
/// <remarks>
/// A failed start can dispose the provider the factory still reads, reporting <see cref="ObjectDisposedException"/>
/// with no inner exception; only a failed start does that, so a re-ask cannot invent a refusal.
/// </remarks>
public static class StartRefusal
{
    public const int Attempts = 5;

    /// <summary>Asks a fresh factory to start until its refusal is not the disposal race.</summary>
    /// <param name="unstartable">A factory that cannot start; each attempt gets its own and disposes it.</param>
    /// <param name="start">The first use that builds the host, such as <c>factory.CreateClient()</c>.</param>
    public static Exception Ask<TFactory>(Func<TFactory> unstartable, Action<TFactory> start)
        where TFactory : IDisposable
    {
        Exception? last = null;

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            using TFactory factory = unstartable();
            Exception? refusal = null;

            try
            {
                start(factory);
            }
            catch (Exception thrown)
            {
                refusal = thrown;
            }

            if (refusal is null)
                throw new InvalidOperationException("The host started, so it refused nothing.");

            if (refusal is not ObjectDisposedException { ObjectName: nameof(IServiceProvider) })
                return refusal;

            last = refusal;
        }

        throw new InvalidOperationException(
            "Every host started here reported a disposed provider, so none of them said why it refused to start.",
            last);
    }
}
