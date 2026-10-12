using Common.Application;

namespace Notifications.Application.Privacy.EndErasedSubject;

/// <summary>Deletes what an event wrote for a subject Notifications had already erased (ADR-097).</summary>
public sealed record EndErasedSubjectCommand(Guid SubjectId) : ICommand<Result>;
