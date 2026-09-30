namespace Shipping.Application.Carrier;

/// <summary>The carrier's answer to a <see cref="CancellationRequest"/>; the case is the whole answer.</summary>
public abstract record CancellationResult
{
    private CancellationResult() { }

    public sealed record Cancelled : CancellationResult;

    public sealed record TooLate : CancellationResult;
}
