namespace Notifications.Application.Privacy;

/// <summary>What Notifications holds of a customer, erased in raw statements on the unit of work's transaction.</summary>
public interface INotificationsPersonalDataStore
{
    /// <summary>Deletes the notices still waiting on the subject, which ADR-052 ends with the record they wait on.</summary>
    Task<int> DeleteWaitingNoticesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Takes the subject's id and the message's values off the ended notices, keeping the evidence (ADR-053).</summary>
    Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Deletes the subject's push deliveries still pending, so none is sent after the erasure.</summary>
    Task<int> DeleteWaitingDeliveriesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Takes the subject's id and languages off the ended deliveries, keeping the evidence.</summary>
    Task<int> AnonymiseEndedDeliveriesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Deletes the subject's order records, which the notices above are found through.</summary>
    Task<int> DeleteOrderRecordsAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Deletes the subject's mailbox and language (ADR-052).</summary>
    Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Clears the subject's device rows as a sign-out does, keeping their sequences (ADR-117).</summary>
    Task<int> ClearDevicesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Takes the subject off every journal entry and stamps when, keeping the rest (ADR-120).</summary>
    Task<int> AnonymiseConsentAsync(Guid subjectId, DateTimeOffset now, CancellationToken ct);
}
