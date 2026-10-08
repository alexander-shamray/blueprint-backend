using Microsoft.AspNetCore.Hosting;

namespace Common.TestSupport;

/// <summary>ADR-079's opt-out, for a test host run outside Development against plaintext containers.</summary>
public static class PlaintextTransport
{
    /// <summary>Names each connection plaintext, as a deployer would, so the check under test fires.</summary>
    public static IWebHostBuilder AcceptPlaintext(this IWebHostBuilder builder, params string[] connections)
    {
        for (int i = 0; i < connections.Length; i++)
            builder.UseSetting($"Transport:Plaintext:{i}", connections[i]);
        return builder;
    }
}
