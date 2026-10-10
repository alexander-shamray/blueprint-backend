using Common.Application;

namespace Privacy.Application.ErasureRequests.MarkOverdue;

/// <summary>Moves one request that has outlived its due time to overdue; the sweep sends one per request (§2.3).</summary>
public sealed record MarkErasureRequestOverdueCommand(Guid RequestId) : ICommand<Result>;
