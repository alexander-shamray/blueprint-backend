using Common.Application;

namespace Ordering.Application.Privacy.CloseErasure;

/// <summary>Stamps the audit row with the close Privacy published (ADR-139).</summary>
public sealed record CloseErasureCommand(Guid RequestId, DateTimeOffset ClosedAt) : ICommand<Result>;
