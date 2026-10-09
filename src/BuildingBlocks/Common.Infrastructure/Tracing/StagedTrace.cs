using System.Diagnostics;

namespace Common.Infrastructure.Tracing;

/// <summary>
/// The W3C context a row was written under, kept on the row so the pass that claims it later runs as that trace's
/// child (§9.4). The outbox stages it on its own columns; a worker's row takes <see cref="StagedTraceColumns"/>.
/// </summary>
/// <remarks>Both are null on a row written outside a W3C activity; its pass starts its own trace (§7.4).</remarks>
public readonly record struct StagedTrace(string? Parent, string? State)
{
    /// <summary>A W3C <c>traceparent</c> at version 00, the only version an activity writes.</summary>
    public const int ParentMaxLength = 55;

    /// <summary>The <c>tracestate</c> W3C asks a vendor to carry at least; a longer one is not staged.</summary>
    public const int StateMaxLength = 512;

    /// <summary>The source a claimed pass starts on; <c>AddObservability</c> names it, or no span starts.</summary>
    public const string ClaimSourceName = "Commerce.Claims";

    private static readonly ActivitySource Claims = new(ClaimSourceName);

    /// <summary>The current activity's context, read where the row is written in that activity's transaction.</summary>
    public static StagedTrace Current
    {
        get
        {
            Activity? staging = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current : null;

            // Not truncated: a cut tracestate is malformed, and a dropped one only loses vendor data.
            return new StagedTrace(
                staging?.Id,
                staging?.TraceStateString is { Length: <= StateMaxLength } state ? state : null);
        }
    }

    /// <summary>The staged context as a parent, or the default context, which starts a trace of its own.</summary>
    public ActivityContext ToParent() =>
        ActivityContext.TryParse(Parent, State, out ActivityContext parsed) ? parsed : default;

    /// <summary>Starts a claimed row's pass as the child of the trace that wrote the row.</summary>
    public Activity? StartClaimed(string name) => Claims.StartActivity(name, ActivityKind.Internal, ToParent());

    /// <summary>Starts a claimed row's pass in a trace of its own, linked to the one that wrote the row.</summary>
    /// <remarks>For a pass repeating over a row's whole life, whose children would stretch its trace (§9.4).</remarks>
    public Activity? StartLinked(string name)
    {
        ActivityContext writer = ToParent();

        return Claims.StartActivity(
            name,
            ActivityKind.Internal,
            parentContext: default,
            links: writer == default ? null : [new ActivityLink(writer)]);
    }
}
