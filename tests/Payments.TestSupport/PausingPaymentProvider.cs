using Payments.Application.Provider;

namespace Payments.TestSupport;

/// <summary>Holds one authorisation open after the provider answers and before its unit commits.</summary>
/// <remarks>The arm lives here because each scope builds its own <see cref="PausingPaymentProvider"/>.</remarks>
public sealed class ProviderGateSeam
{
    private ProviderGate? _armed;

    /// <summary>Arms the next authorisation, one gate at a time.</summary>
    public ProviderGate PauseNextAuthorisation()
    {
        ProviderGate gate = new(this);
        if (Interlocked.CompareExchange(ref _armed, gate, null) is not null)
            throw new InvalidOperationException("A provider gate is already armed.");

        return gate;
    }

    internal void Disarm(ProviderGate gate) => Interlocked.CompareExchange(ref _armed, null, gate);

    /// <summary>Takes the arm rather than reading it, so the gate is one call's, not the seam's.</summary>
    internal ProviderGate? Take() => Interlocked.Exchange(ref _armed, null);
}

/// <summary>The registered adapter, paused after it answers by <see cref="ProviderGateSeam"/>.</summary>
public sealed class PausingPaymentProvider(IPaymentProvider inner, ProviderGateSeam seam) : IPaymentProvider
{
    public async Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct)
    {
        AuthorisationResult result = await inner.AuthoriseAsync(request, ct);

        ProviderGate? gate = seam.Take();
        if (gate is not null)
            await gate.HoldAsync(ct);

        return result;
    }

    public Task VoidAsync(VoidRequest request, CancellationToken ct) => inner.VoidAsync(request, ct);
}

/// <summary>One armed pause; disposing releases it, so a failing test cannot leave a consumer parked.</summary>
public sealed class ProviderGate(ProviderGateSeam owner) : IDisposable
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the authorisation has answered and is waiting here.</summary>
    public Task Reached => _reached.Task;

    public void Release() => _released.TrySetResult();

    public void Dispose()
    {
        owner.Disarm(this);
        Release();
    }

    internal async Task HoldAsync(CancellationToken ct)
    {
        _reached.TrySetResult();
        await _released.Task.WaitAsync(ct);
    }
}
