using Notifications.Application.Rendering;

namespace Notifications.Application.Preferences;

/// <summary>ADR-120's closed key: the topics a subject may choose for and the channels each reaches them on.</summary>
/// <remarks>
/// Every topic is a service topic today, one per order event; a promotional class is a new ADR before it is a value
/// here (ADR-053 rule 4), and the class is what decides a pair's default.
/// </remarks>
public static class ConsentVocabulary
{
    public const string Email = "email";

    public const string Push = "push";

    /// <summary>The widest topic name, the journal's column width.</summary>
    public const int MaxTopicLength = 64;

    /// <summary>The widest channel name, the journal's column width.</summary>
    public const int MaxChannelLength = 8;

    /// <summary>The <c>azp</c> claim a write is recorded under: a Keycloak client id, at most 255 characters.</summary>
    public const int MaxClientLength = 255;

    public static readonly IReadOnlyList<string> Channels = [Email, Push];

    /// <summary>The seven order events, each a service message, named by its template key.</summary>
    public static readonly IReadOnlyList<string> ServiceTopics =
    [
        TemplateKeys.OrderPlaced,
        TemplateKeys.OrderConfirmed,
        TemplateKeys.OrderCancelled,
        TemplateKeys.PaymentDeclined,
        TemplateKeys.PaymentRefunded,
        TemplateKeys.ShipmentDispatched,
        TemplateKeys.ShipmentDelivered
    ];

    /// <summary>Every topic, in the order the preferences read lists them.</summary>
    public static IReadOnlyList<string> Topics => ServiceTopics;

    public static bool IsTopic(string? topic) => topic is not null && Topics.Contains(topic);

    public static bool IsChannel(string? channel) => channel is not null && Channels.Contains(channel);

    /// <summary>A service topic defaults on, on every channel; any other class defaults off, always.</summary>
    public static bool IsService(string topic) => ServiceTopics.Contains(topic);
}
