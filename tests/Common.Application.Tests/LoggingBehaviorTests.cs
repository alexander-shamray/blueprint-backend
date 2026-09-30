using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

public class LoggingBehaviorTests
{
    private static ServiceProvider Build() =>
        TestContainer.Build(services =>
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>)));

    [Fact]
    public async Task A_completed_request_is_logged_with_the_time_it_took()
    {
        using ServiceProvider provider = Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new Tick(), TestContext.Current.CancellationToken);

        LogLine line = scope.ServiceProvider.GetRequiredService<LogSink>().Lines.Single();

        line.Level.ShouldBe(LogLevel.Information);
        line.Message.ShouldBe("Tick completed in 250 ms");
        line.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task The_request_type_is_pushed_as_a_scope_and_not_as_a_property()
    {
        // A scope, so everything written inside the handler inherits it (§13.3).
        using ServiceProvider provider = Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new Tick(), TestContext.Current.CancellationToken);

        object? pushed = scope.ServiceProvider.GetRequiredService<LogSink>().Scopes.Single();

        pushed.ShouldBeOfType<Dictionary<string, object>>()["RequestType"].ShouldBe("Tick");
    }

    [Fact]
    public async Task A_completed_request_records_an_ok_outcome()
    {
        using MeasurementCollector collector = MeasurementCollector.ForRequests();
        using ServiceProvider provider = Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new Tick(), TestContext.Current.CancellationToken);

        RecordedMeasurement measurement = collector.For(nameof(Tick));

        measurement.Instrument.ShouldBe("request.duration");
        measurement.Value.ShouldBe(0.25);
        measurement.Tags["outcome"].ShouldBe("ok");
    }

    [Fact]
    public async Task A_throwing_request_is_logged_as_an_error_and_rethrown()
    {
        using MeasurementCollector collector = MeasurementCollector.ForRequests();
        using ServiceProvider provider = Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(new Boom(), TestContext.Current.CancellationToken));

        LogLine line = scope.ServiceProvider.GetRequiredService<LogSink>().Lines.Single();

        line.Level.ShouldBe(LogLevel.Error);
        line.Exception.ShouldBeOfType<InvalidOperationException>();
        collector.For(nameof(Boom)).Tags["outcome"].ShouldBe("error");
    }

    [Fact]
    public async Task A_command_the_domain_rejected_is_still_an_ok_outcome()
    {
        using MeasurementCollector collector = MeasurementCollector.ForRequests();
        using ServiceProvider provider = Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result result = await dispatcher.SendAsync(new Reject(), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        collector.For(nameof(Reject)).Tags["outcome"].ShouldBe("ok");
    }
}
