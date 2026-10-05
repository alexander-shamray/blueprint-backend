namespace Common.Web;

/// <summary>§15.4's static constants: settings that would not differ between environments.</summary>
public static class ServiceOptions
{
    /// <summary>The deadline a service's request meets, above every outbound total (§9.7).</summary>
    /// <remarks>Below the gateway's own deadline and inside <c>HostOptions.ShutdownTimeout</c> (ADR-066).</remarks>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
}
