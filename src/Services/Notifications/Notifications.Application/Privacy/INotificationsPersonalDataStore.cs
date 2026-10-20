namespace Notifications.Application.Privacy;

/// <summary>What Notifications holds of a customer, erased in statements on the unit of work's transaction.</summary>
/// <remarks>
/// Each subject-wide statement that takes the request keeps what was written under it: the records, notices and
/// deliveries of an order placed under it (ADR-099), and a device or consent row stamped with it (ADR-138).
/// </remarks>
public interface INotificationsPersonalDataStore
{
    /// <summary>Deletes the notices still waiting on the subject, which ADR-052 ends with the record they wait
    /// on.</summary>
    Task<int> DeleteWaitingNoticesAsync(Guid subjectId, Guid requestId, CancellationToken ct);

    /// <summary>Takes the subject's id and the message's values off the ended notices, keeping the evidence
    /// (ADR-053).</summary>
    Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, Guid requestId, CancellationToken ct);

    /// <summary>Deletes the subject's push deliveries still pending, so none is sent after the erasure.</summary>
    Task<int> DeleteWaitingDeliveriesAsync(Guid subjectId, Guid requestId, CancellationToken ct);

    /// <summary>Takes the subject's id and languages off the ended deliveries, keeping the evidence.</summary>
    Task<int> AnonymiseEndedDeliveriesAsync(Guid subjectId, Guid requestId, CancellationToken ct);

    /// <summary>Deletes the subject's order records, which the notices above are found through.</summary>
    Task<int> DeleteOrderRecordsAsync(Guid subjectId, Guid requestId, CancellationToken ct);

    /// <summary>Deletes the subject's mailbox and language (ADR-052).</summary>
    Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Deletes the notices still waiting on one order.</summary>
    Task<int> DeleteWaitingOrderNoticesAsync(Guid orderId, CancellationToken ct);

    /// <summary>Takes the customer's id and the message's values off one order's ended notices (ADR-053).</summary>
    Task<int> AnonymiseEndedOrderNoticesAsync(Guid orderId, CancellationToken ct);

    /// <summary>Deletes the push deliveries still pending on one order's notices.</summary>
    Task<int> DeleteWaitingOrderDeliveriesAsync(Guid orderId, CancellationToken ct);

    /// <summary>Takes the customer's id and languages off the ended deliveries of one order's notices.</summary>
    Task<int> AnonymiseEndedOrderDeliveriesAsync(Guid orderId, CancellationToken ct);

    /// <summary>Deletes one order's record, if it is still the subject's.</summary>
    Task<int> DeleteOrderRecordAsync(Guid subjectId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// Clears the subject's device rows not registered under the request as a sign-out does, keeping their sequences
    /// (ADR-117, ADR-138).
    /// </summary>
    Task<int> ClearDevicesAsync(Guid subjectId, Guid requestId, CancellationToken ct);

    /// <summary>
    /// Takes the subject off each entry not written under the request, and stamps when (ADR-120, ADR-138).
    /// </summary>
    Task<int> AnonymiseConsentAsync(Guid subjectId, Guid requestId, DateTimeOffset now, CancellationToken ct);
}
