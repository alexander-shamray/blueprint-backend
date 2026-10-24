using Common.Infrastructure.Outbox;
using Shouldly;

namespace Notifications.Worker.Tests;

/// <summary>Where a first fault leaves a row's NextAttemptAt, on the engine's clock read either side of the pass.</summary>
internal static class BackOffWindow
{
    public static void ShouldBeFirstStep(DateTimeOffset nextAttemptAt, DateTimeOffset before, DateTimeOffset after) =>
        nextAttemptAt.ShouldBeInRange(
            before.AddSeconds(OutboxDispatcher.BackoffBaseSeconds),
            after.AddSeconds(OutboxDispatcher.BackoffBaseSeconds),
            "the first fault waits the ladder's first step, not none and not a later one");
}
