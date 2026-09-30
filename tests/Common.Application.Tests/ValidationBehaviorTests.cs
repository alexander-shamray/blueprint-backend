using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

public class ValidationBehaviorTests
{
    private static ServiceProvider Build() =>
        TestContainer.Build(services =>
        {
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
            services.AddScoped<IValidator<Ping>, PingValidator>();
            services.AddScoped<IValidator<Ping>, PingLengthValidator>();
        });

    [Fact]
    public async Task A_valid_request_reaches_its_handler()
    {
        using ServiceProvider provider = Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.SendAsync(new Ping("hello"), TestContext.Current.CancellationToken);

        result.ShouldBe("pong:hello");
    }

    [Fact]
    public async Task An_invalid_request_never_reaches_its_handler()
    {
        using ServiceProvider provider = Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<ValidationException>(
            () => dispatcher.SendAsync(new Ping(""), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_validator_runs_before_the_first_failure_is_reported()
    {
        using ServiceProvider provider = Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        ValidationException thrown = await Should.ThrowAsync<ValidationException>(
            () => dispatcher.SendAsync(new Ping(""), TestContext.Current.CancellationToken));

        string[] codes = [.. thrown.Errors.Select(f => f.ErrorCode)];

        codes.ShouldBe(["Empty", "TooShort"], ignoreOrder: true);
    }

    [Fact]
    public async Task Enumerating_the_injected_validators_twice_constructs_them_once()
    {
        // §6.3 reads the sequence twice, safe only because the container materialises IEnumerable<T> first.
        using ServiceProvider provider = TestContainer.Build(services =>
        {
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
            services.AddScoped<ValidatorConstructions>();
            services.AddScoped<IValidator<Ask>, CountingValidator>();
        });

        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.QueryAsync(new Ask("why"), TestContext.Current.CancellationToken);

        scope.ServiceProvider.GetRequiredService<ValidatorConstructions>().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_request_with_no_validator_passes_straight_through()
    {
        using ServiceProvider provider = Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.QueryAsync(new Ask("why"), TestContext.Current.CancellationToken);

        result.ShouldBe("answer:why");
    }
}
