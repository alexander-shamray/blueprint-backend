using System.Diagnostics.CodeAnalysis;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Contacts;
using Notifications.Application.Devices;
using Notifications.Application.Records;
using Notifications.Application.Rendering;

namespace Notifications.Application.Delivery;

/// <summary>The send worker's decisions, each a pure function over what a pass has read (ADR-049, ADR-052).</summary>
/// <remarks>
/// Here rather than in the worker, as §4.1 gives the service no Domain project: the Application suite drives each
/// row, and the worker only reads, calls and commits.
/// </remarks>
public static class SendRules
{
    /// <summary>Whether a row has waited out the give-up age, measured from its creation (ADR-052).</summary>
    public static bool HasGivenUp(DateTimeOffset createdAt, DateTimeOffset now, TimeSpan giveUpAge) =>
        now - createdAt >= giveUpAge;

    /// <summary>A row waits for Ordering's record, and a decline for its cancellation too (ADR-049).</summary>
    public static bool AwaitsOrderRecord(string templateKey, [NotNullWhen(false)] OrderRecord? order) =>
        order is null || (templateKey == TemplateKeys.PaymentDeclined && order.CancelledAt is null);

    /// <summary>A decline is never sent to a customer who cancelled; nothing else is held back (ADR-049).</summary>
    public static bool Suppresses(string templateKey, OrderRecord order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return templateKey == TemplateKeys.PaymentDeclined && CustomerCancelled(order);
    }

    /// <summary>Whether the customer cancelled: any origin but the workflow's, never the reason (ADR-049).</summary>
    public static bool CustomerCancelled(OrderRecord order)
    {
        ArgumentNullException.ThrowIfNull(order);

        // The reason is what somebody asserted (§9.6), so an absent or third origin is the customer's; a workflow
        // cancellation from an older publisher loses only the decline, as the cancellation notice gives the reason.
        return order.CancelledAt is not null && order.CancelOrigin != CancelOrigins.Workflow;
    }

    /// <summary>Whether a delivery's device still reaches its customer on the platform it was fanned out for.</summary>
    /// <remarks>
    /// Gone, cleared, another subject's or another platform's, it does not: a device ADR-117 moved to another subject
    /// must never show the subject before it a notice of theirs.
    /// </remarks>
    public static bool Reaches(PushDevice? device, Guid customerId, string platform) =>
        device is { Token: not null, AppId: not null } &&
        device.SubjectId == customerId &&
        string.Equals(device.Platform, platform, StringComparison.Ordinal);

    /// <summary>A stored contact against ADR-052's two numbers; the ceiling's instant is not served.</summary>
    public static ContactAge AgeOf(ContactRecord? contact, DateTimeOffset now, ContactOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (contact is null)
            return ContactAge.Absent;

        TimeSpan age = now - contact.FetchedAt;

        if (age < options.Freshness)
            return ContactAge.Fresh;

        return age < options.StaleCeiling ? ContactAge.Stale : ContactAge.Expired;
    }
}
