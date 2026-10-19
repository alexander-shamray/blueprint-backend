namespace Ordering.Application.Privacy;

/// <summary>What Ordering holds of a customer, erased in raw statements on the unit of work's transaction.</summary>
public interface IOrderPersonalDataStore
{
    /// <summary>
    /// Replaces the subject's id and the address on every order not placed under the request, keeping the financial
    /// record (ADR-099).
    /// </summary>
    Task<int> AnonymiseOrdersAsync(Guid subjectId, Guid requestId, CancellationToken ct);

    /// <summary>
    /// Takes the subject's id off the summaries of the orders the request erased, keeping the counted-once flags a
    /// delete would reset.
    /// </summary>
    Task<int> AnonymiseSummariesAsync(Guid subjectId, Guid requestId, CancellationToken ct);
}
