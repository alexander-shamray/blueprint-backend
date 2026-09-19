using Payments.Application.Provider;

namespace Payments.TestSupport;

/// <summary>
/// Holds one authorisation open after the provider has answered and before its
/// unit commits, so a test owns the interleaving rather than the scheduler
/// (spec, section 6). Test support only; the host never registers it.
/// </summary>
/// <remarks>
/// The arm lives here rather than on the decorator: the adapter is a typed
/// client, so a consumer's scope builds its own decorator and an arm stored
/// there would be invisible to it.
/// </remarks>
public sealed class ProviderGateSeam
{
    private ProviderGate? _armed;

    /// <summary>
    /// Arms the next authorisation. One gate at a time, because two armed at
    /// once would leave which of them caught the call to the order of the
    /// calls.
    /// </summary>
    public ProviderGate PauseNextAuthorisation()
    {
        ProviderGate gate = new(this);
        if (Interlocked.CompareExchange(ref _armed, gate, null) is not null)
            throw new InvalidOperationException("A provider gate is already armed.");

        return gate;
    }

    internal void Disarm(ProviderGate gate) => Interlocked.CompareExchange(ref _armed, null, gate);

    /// <summary>
    /// Takes the arm rather than reading it, so a second authorisation in the
    /// same run passes straight through: the gate is one call's, not the
    /// seam's.
    /// </summary>
    internal ProviderGate? Take() => Interlocked.Exchange(ref _armed, null);
}

/// <summary>
/// The registered adapter, paused on demand by <see cref="ProviderGateSeam"/>.
/// </summary>
/// <remarks>
/// The pause is after the inner call deliberately: that is the window an
/// unlocked read would let a cancellation through — it would find no intent,
/// void nothing, and leave money held on a cancelled order.
/// </remarks>
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

/// <summary>
/// One armed pause. <see cref="Reached"/> completes when the authorisation
/// arrives at it, and the call stays parked until <see cref="Release"/> or
/// disposal.
/// </summary>
/// <remarks>
/// Disposing releases, so a failing test cannot leave a consumer parked and
/// the broker redelivering into a unit that never finishes.
/// </remarks>
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
