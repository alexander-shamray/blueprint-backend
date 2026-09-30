using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>The two Kestrel endpoints, read off the host's configuration, the text the server binds (§9.7).</summary>
public sealed class KestrelEndpointTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IConfiguration Configuration => factory.Services.GetRequiredService<IConfiguration>();

    [Fact]
    public void The_rest_surface_stays_on_8080_over_http1()
    {
        Configuration["Kestrel:Endpoints:Rest:Url"].ShouldBe("http://0.0.0.0:8080");
        Configuration["Kestrel:Endpoints:Rest:Protocols"].ShouldBe("Http1");
    }

    [Fact]
    public void The_grpc_surface_is_8081_and_http2_only()
    {
        Configuration["Kestrel:Endpoints:Grpc:Url"].ShouldBe("http://0.0.0.0:8081");
        Configuration["Kestrel:Endpoints:Grpc:Protocols"].ShouldBe(
            "Http2",
            "a cleartext endpoint at Http1AndHttp2 refuses a client asking for HTTP/2 exactly, " +
            "so the address read would fail at the connection (§9.7)");
    }
}
