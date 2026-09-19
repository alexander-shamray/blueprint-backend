namespace Payments.Application.Provider;

/// <summary>The provider's verdict on an <see cref="AuthorisationRequest"/>.</summary>
public abstract record AuthorisationResult
{
    private AuthorisationResult() { }

    public sealed record Authorised(string Reference) : AuthorisationResult;

    public sealed record Declined(string Reason) : AuthorisationResult;
}
