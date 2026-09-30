namespace Common.Web;

/// <summary>§15.4's static constants: settings that would not differ between environments.</summary>
public static class ServiceOptions
{
    /// <summary>The ceiling §9.7's timeout hierarchy asserts the outbound total against.</summary>
    /// <remarks>Strictly under the gateway's band and not enforced at runtime (§9.7).</remarks>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
}
