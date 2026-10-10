namespace Privacy.Api;

/// <summary>Privacy's permission vocabulary (§11.4), the contract with the realm's claim mapper (§11.5).</summary>
/// <remarks>A constant, so the policy and the endpoint cannot spell it differently (§11.4).</remarks>
public static class PrivacyPermissions
{
    /// <summary>Raise an erasure request for a customer and read where it stands.</summary>
    public const string Erase = "privacy:erase";
}
