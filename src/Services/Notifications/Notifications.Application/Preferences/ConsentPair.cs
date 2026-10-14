using System.Diagnostics.CodeAnalysis;

namespace Notifications.Application.Preferences;

/// <summary>One key of ADR-120's journal: a topic and the channel it reaches the subject on.</summary>
/// <remarks>Written <c>topic/channel</c>, the order the preferences route names them in (§10.11).</remarks>
public readonly record struct ConsentPair(string Topic, string Channel)
{
    public const char Separator = '/';

    /// <summary>A pair of the vocabulary's, or false; the parse checks the words as well as the shape.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ConsentPair? pair)
    {
        pair = null;
        string[] parts = text?.Split(Separator) ?? [];

        if (parts is not [{ } topic, { } channel] ||
            !ConsentVocabulary.IsTopic(topic) ||
            !ConsentVocabulary.IsChannel(channel))
        {
            return false;
        }

        pair = new ConsentPair(topic, channel);
        return true;
    }

    public override string ToString() => $"{Topic}{Separator}{Channel}";
}
