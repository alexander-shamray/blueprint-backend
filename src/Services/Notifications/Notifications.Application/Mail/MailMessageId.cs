namespace Notifications.Application.Mail;

/// <summary>A sent message's <c>Message-ID</c> left of the <c>@</c>: the row's event and its template key.</summary>
public sealed record MailMessageId
{
    public MailMessageId(Guid eventId, string templateKey)
    {
        ArgumentNullException.ThrowIfNull(templateKey);

        if (eventId == Guid.Empty)
            throw new ArgumentException("The empty id is no event.", nameof(eventId));

        // Kebab case alone, so nothing but these characters ever reaches a header.
        if (!IsKebabCase(templateKey))
            throw new ArgumentException("A template key is kebab case.", nameof(templateKey));

        EventId = eventId;
        TemplateKey = templateKey;
    }

    public Guid EventId { get; }

    public string TemplateKey { get; }

    public string LocalPart => $"{EventId:N}.{TemplateKey}";

    private static bool IsKebabCase(string key) =>
        key.Length > 0 &&
        key[0] != '-' &&
        key[^1] != '-' &&
        !key.Contains("--", StringComparison.Ordinal) &&
        key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
