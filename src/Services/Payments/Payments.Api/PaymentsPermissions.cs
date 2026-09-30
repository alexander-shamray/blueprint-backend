namespace Payments.Api;

/// <summary>Payments' permission vocabulary (§11.4), re-validating the gateway's own (§10.2, §11.3).</summary>
public static class PaymentsPermissions
{
    public const string Admin = "payments:admin";
}
