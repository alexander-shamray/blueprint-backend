namespace Ordering.Domain.Orders;

/// <summary>Who asked for a cancellation, beside <see cref="CancellationReason"/>, which says why.</summary>
/// <remarks>Recorded, not enforced: §11.4 accepts any reason, so only this tells §9.6 who asked.</remarks>
public enum CancellationOrigin
{
    /// <summary>A request with a principal behind it, through §11.4's endpoint.</summary>
    /// <remarks>The zero value, so an origin nobody set makes §9.6's missing-instance branch fault (§11.4).</remarks>
    User,

    /// <summary>§9.6's saga compensating; named for the actor, where <c>CommandOrigin</c> names the path.</summary>
    Workflow
}
