namespace BffReplay;

/// <summary>What a replay sent: the ids, a count per type, and how far back each publisher's window reached.</summary>
public sealed class ReplayReport
{
    private readonly List<Guid> _sent = [];

    private readonly SortedDictionary<string, int> _byType = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (int Count, DateTimeOffset Oldest)> _byPublisher = [];

    public IReadOnlyList<Guid> SentMessageIds => _sent;

    public IReadOnlyDictionary<string, int> SentByType => _byType;

    /// <summary>Rows in the window the BFF's inbox had already recorded, which a repair leaves unsent.</summary>
    public int SkippedCount { get; private set; }

    /// <summary>The oldest <c>OccurredAt</c> a publisher held: the window this replay reached there.</summary>
    public DateTimeOffset? OldestFrom(Publisher publisher) =>
        _byPublisher.TryGetValue(publisher.Name, out (int Count, DateTimeOffset Oldest) reached)
            ? reached.Oldest
            : null;

    /// <summary>One line per publisher, printed as each finishes, so a run that fails says how far it got.</summary>
    public string Describe(Publisher publisher) =>
        _byPublisher.TryGetValue(publisher.Name, out (int Count, DateTimeOffset Oldest) reached)
            ? $"{publisher.Name}: {reached.Count} event(s) sent, the oldest from {reached.Oldest:O}."
            : $"{publisher.Name}: no event of ADR-051's eight sent from its outbox window.";

    internal void Skipped() => SkippedCount++;

    internal void Sent(Publisher publisher, string messageType, OutboxRow row)
    {
        _sent.Add(row.MessageId);
        _byType[messageType] = _byType.GetValueOrDefault(messageType) + 1;

        (int Count, DateTimeOffset Oldest) reached = _byPublisher.GetValueOrDefault(
            publisher.Name,
            (0, DateTimeOffset.MaxValue));
        _byPublisher[publisher.Name] =
            (reached.Count + 1, row.OccurredAt < reached.Oldest ? row.OccurredAt : reached.Oldest);
    }
}
