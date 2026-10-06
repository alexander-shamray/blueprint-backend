using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Common.Web;

/// <summary>A host's own readiness probe, for a container image that has no shell or HTTP client (§14.1).</summary>
/// <remarks>The port is a declared Kestrel endpoint's, which outranks the image's ASPNETCORE_HTTP_PORTS, else that
/// variable's; the path is the one <see cref="HealthCheckExtensions.MapCommonHealthEndpoints"/> maps (§13.5).</remarks>
public static class HealthProbe
{
    /// <summary>The argument that makes a host probe itself instead of starting.</summary>
    public const string Argument = "--probe";

    private const string ReadinessPath = "/health/ready";

    // Shorter than the Compose healthcheck's own timeout, so the probe answers before Docker kills it.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Asks this container's readiness endpoint over loopback: 0 on 200, 1 on anything else.</summary>
    public static Task<int> RunAsync() => RunAsync(PortOf(HostConfiguration()), Timeout);

    /// <summary>Asks the readiness endpoint on <paramref name="port"/> over loopback.</summary>
    public static async Task<int> RunAsync(int? port, TimeSpan timeout)
    {
        if (port is null)
        {
            await Console.Error.WriteLineAsync(
                "No port to probe: no HTTP/1.1 endpoint in Kestrel:Endpoints and no ASPNETCORE_HTTP_PORTS.");
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

    /// <summary>The first HTTP/1.1 endpoint's port when any is declared, else HTTP_PORTS' first; null if none.</summary>
    /// <remarks>Any declared endpoint replaces HTTP_PORTS, and an Http2-only one, §9.7's gRPC hop, refuses the
    /// probe's HTTP/1.1.</remarks>
    public static int? PortOf(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection[] endpoints = [.. configuration.GetSection("Kestrel:Endpoints").GetChildren()];
        if (endpoints.Length > 0)
        {
            string? url = endpoints
                .Where(e => !string.Equals(e["Protocols"], "Http2", StringComparison.OrdinalIgnoreCase))
                .Select(e => e["Url"])
                .FirstOrDefault(u => u is not null);

            return url is null ? null : BindingAddress.Parse(url).Port;
        }

        string first = (configuration["HTTP_PORTS"] ?? string.Empty).Split(';', StringSplitOptions.TrimEntries)[0];
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : null;
    }

    // The JSON and environment sources, layered as a container host layers them; no host is built to ask.
    private static IConfiguration HostConfiguration()
    {
        string environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            "Production";

        return new ConfigurationBuilder()
            .AddEnvironmentVariables("ASPNETCORE_")
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }
}
