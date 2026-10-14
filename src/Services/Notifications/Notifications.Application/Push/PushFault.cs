namespace Notifications.Application.Push;

/// <summary>Why a push met a fault rather than an answer, which decides its retry and its count.</summary>
public enum PushFault
{
    /// <summary>No request reached the provider, or it said it took nothing: a 429, a 503, or its own code.</summary>
    Transient,

    /// <summary>A timeout once the request started, or a 5xx that does not say it took nothing (§9.7).</summary>
    Unconfirmed,

    /// <summary>The handshake failed, which is a deployment's trust store or a provider's certificate.</summary>
    Tls,

    /// <summary>The provider refused this deployment's credential, a deployment's fault and not the device's.</summary>
    Credential,

    /// <summary>The provider refused the request itself, as it does a malformed payload or token.</summary>
    Rejected
}
