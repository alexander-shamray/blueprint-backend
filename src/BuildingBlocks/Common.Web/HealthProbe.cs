using System.Globalization;
using System.Net;

namespace Common.Web;

/// <summary>A host's own readiness probe, for a container image that has no shell or HTTP client (§14.1).</summary>
/// <remarks>The port is the runtime image's ASPNETCORE_HTTP_PORTS, which Catalog's and Ordering's Kestrel:Endpoints
/// pin alike, and the path is the one <see cref="HealthCheckExtensions.MapCommonHealthEndpoints"/> maps (§13.5).
/// </remarks>
public static class HealthProbe
{
    /// <summary>The argument that makes a host probe itself instead of starting.</summary>
    public const string Argument = "--probe";

    private const string ReadinessPath = "/health/ready";

    // Shorter than the Compose healthcheck's own timeout, so the probe answers before Docker kills it.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Asks this container's readiness endpoint over loopback: 0 on 200, 1 on anything else.</summary>
    public static Task<int> RunAsync() =>
        RunAsync(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"), Timeout);

    /// <summary>Asks the readiness endpoint on the first of <paramref name="httpPorts"/> over loopback.</summary>
    public static async Task<int> RunAsync(string? httpPorts, TimeSpan timeout)
    {
        string first = (httpPorts ?? string.Empty).Split(';', StringSplitOptions.TrimEntries)[0];
        if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            await Console.Error.WriteLineAsync($"No port to probe: ASPNETCORE_HTTP_PORTS is '{httpPorts}'.");
            return 1;
        }

        using HttpClient client = new() { Timeout = timeout };
        try
        {
            using HttpResponseMessage response =
                await client.GetAsync(new Uri($"http://127.0.0.1:{port}{ReadinessPath}"));
            await Console.Out.WriteLineAsync($"{ReadinessPath} answered {(int)response.StatusCode}.");
            return response.StatusCode == HttpStatusCode.OK ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"{ReadinessPath} did not answer: {e.Message}");
            return 1;
        }
    }
}
