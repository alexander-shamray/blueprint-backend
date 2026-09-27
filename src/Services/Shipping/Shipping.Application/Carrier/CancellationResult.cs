namespace Shipping.Application.Carrier;

/// <summary>
/// The carrier's answer to a <see cref="CancellationRequest"/>. Neither case
/// carries anything: the reference is the caller's, and the reason is the
/// state rather than a string.
/// </summary>
public abstract record CancellationResult
{
    private CancellationResult() { }

    public sealed record Cancelled : CancellationResult;

    public sealed record TooLate : CancellationResult;
}
