namespace Notifications.Application.Privacy;

/// <summary>What Notifications holds of a customer, erased in raw statements on the unit of work's transaction.</summary>
public interface INotificationsPersonalDataStore
{
    /// <summary>Deletes the notices still waiting on the subject, which ADR-052 ends with the record they wait on.</summary>
    Task<int> DeleteWaitingNoticesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Takes the subject's id and the message's values off the ended notices, keeping the evidence (ADR-053).</summary>
    Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Deletes the subject's order records, which the notices above are found through.</summary>
    Task<int> DeleteOrderRecordsAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Deletes the subject's mailbox and language (ADR-052).</summary>
    Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct);
}
