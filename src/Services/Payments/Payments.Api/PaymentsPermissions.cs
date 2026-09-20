namespace Payments.Api;

/// <summary>
/// Payments' permission vocabulary (§11.4): one name, the gateway's own
/// <c>payments:admin</c> (§10.2), re-validated here because §11.3 makes every
/// service check its own token.
/// </summary>
public static class PaymentsPermissions
{
    public const string Admin = "payments:admin";
}
