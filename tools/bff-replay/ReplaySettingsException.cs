namespace BffReplay;

/// <summary>A replay refused before it opens anything, naming every key it was not given.</summary>
public sealed class ReplaySettingsException : Exception
{
    public ReplaySettingsException()
    {
        Missing = [];
    }

    public ReplaySettingsException(string message)
        : base(message)
    {
        Missing = [];
    }

    public ReplaySettingsException(string message, Exception innerException)
        : base(message, innerException)
    {
        Missing = [];
    }

    public ReplaySettingsException(IReadOnlyList<string> missing)
        : base(
            $"bff-replay needs {string.Join(", ", missing)} and was not given them. Nothing was read, " +
            "deleted or sent; tools/bff-replay/README.md says which login each one is.")
    {
        Missing = missing;
    }

    /// <summary>The keys absent or blank, all of them, so one run names every fix.</summary>
    public IReadOnlyList<string> Missing { get; }
}
