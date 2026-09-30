using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Common.Web.Tests;

/// <summary>The real middleware pipeline in memory, since its pieces only fail together (§10.4, §10.5).</summary>
internal static class TestPipeline
{
    internal static Task<IHost> StartAsync(RequestDelegate terminal, ILoggerProvider? logs = null) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                // Activity.Current is an AsyncLocal, which reaches the request only with this.
                web.UseTestServer(options => options.PreserveExecutionContext = true);

                web.ConfigureServices(services => services.AddCommonProblemDetails());

                web.Configure(app =>
                {
                    app.UseCorrelationId();
                    app.Run(terminal);
                });
            })
            .ConfigureLogging(logging =>
                logging.ClearProviders().AddProvider(logs ?? NullLoggerProvider.Instance))
            .StartAsync();
}
