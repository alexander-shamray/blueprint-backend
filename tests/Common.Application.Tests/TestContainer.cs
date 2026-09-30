using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Common.Application.Tests;

/// <summary>The registration path every test takes, standing in for §4.2's <c>AddOrderingApplication</c>.</summary>
internal static class TestContainer
{
    internal static ServiceProvider Build(Action<IServiceCollection>? behaviours = null)
    {
        ServiceCollection services = new();

        services.AddDispatcher();
        services.AddPluggableFrom(typeof(Ping).Assembly);

        // One clock under two service types; two registrations would make every elapsed time zero.
        services.AddSingleton<FakeTimeProvider>();
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());

        services.AddSingleton<LogSink>();
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        services.AddSingleton<IMeterFactory, TestMeterFactory>();
        services.AddSingleton<RequestMetrics>();

        services.AddScoped<PipelineLog>();
        services.AddScoped<ScopeMarker>();

        services.AddScoped<IdempotencyContext>();

        // By hand, because registration order is pipeline order (§6.3).
        behaviours?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
