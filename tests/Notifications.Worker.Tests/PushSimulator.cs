using WireMock.Server;
using WireMock.Settings;

namespace Notifications.Worker.Tests;

/// <summary>One WireMock.Net server loaded with the Compose simulator's own mappings (§12.7).</summary>
public sealed class PushSimulator : IDisposable
{
    public PushSimulator()
    {
        // Loopback, not WireMock's default of every interface, which a workstation firewall stops to ask about.
        Server = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
        Reset();
    }

    public WireMockServer Server { get; }

    public string BaseUrl => Server.Urls[0] + "/";

    public static string Mappings() =>
        Path.Combine(RepositoryRoot.Locate(), "deploy", "compose", "push-simulator", "mappings");

    /// <summary>The simulator's files alone: no stub another test added, and no request it made.</summary>
    public void Reset()
    {
        Server.ResetLogEntries();
        Server.ResetMappings();
        Server.ReadStaticMappings(Mappings());
    }

    public void Dispose() => Server.Stop();
}
