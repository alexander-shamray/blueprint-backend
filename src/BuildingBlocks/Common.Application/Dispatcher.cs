using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Application;

/// <summary>Runs a request through its behaviours to its handler, caching one invoker per shape (§6.2).</summary>
internal sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    // Keyed on all three parts: a request may implement ICommand<T> under two results, or ICommand<T> and
    // IQuery<T> under one, and a shorter key would hand it the other shape's invoker.
    private static readonly ConcurrentDictionary<(Type Request, Type Result, Type Kind), object> Invokers = new();

    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
        GetInvoker<TResult>(command.GetType(), typeof(CommandInvoker<,>))
            .InvokeAsync(services, command, ct);

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default) =>
        GetInvoker<TResult>(query.GetType(), typeof(QueryInvoker<,>))
            .InvokeAsync(services, query, ct);

    private static Invoker<TResult> GetInvoker<TResult>(Type requestType, Type openInvoker) =>
        (Invoker<TResult>)Invokers.GetOrAdd(
            (requestType, typeof(TResult), openInvoker),
            static key => Activator.CreateInstance(key.Kind.MakeGenericType(key.Request, key.Result))!);

    private abstract class Invoker<TResult>
    {
        public abstract Task<TResult> InvokeAsync(IServiceProvider services, object request, CancellationToken ct);
    }

    private sealed class CommandInvoker<TCommand, TResult> : Invoker<TResult>
        where TCommand : ICommand<TResult>
    {
        public override Task<TResult> InvokeAsync(IServiceProvider services, object request, CancellationToken ct)
        {
            TCommand typed = (TCommand)request;
            ICommandHandler<TCommand, TResult> handler =
                services.GetRequiredService<ICommandHandler<TCommand, TResult>>();

            NextDelegate<TResult> pipeline = () => handler.HandleAsync(typed, ct);

            // Reversed so the first-registered behaviour is the outermost (§6.3).
            foreach (IPipelineBehavior<TCommand, TResult> behavior in services
                .GetServices<IPipelineBehavior<TCommand, TResult>>()
                .Reverse())
            {
                NextDelegate<TResult> next = pipeline;
                pipeline = () => behavior.HandleAsync(typed, next, ct);
            }

            return pipeline();
        }
    }

    private sealed class QueryInvoker<TQuery, TResult> : Invoker<TResult>
        where TQuery : IQuery<TResult>
    {
        public override Task<TResult> InvokeAsync(IServiceProvider services, object request, CancellationToken ct)
        {
            TQuery typed = (TQuery)request;
            IQueryHandler<TQuery, TResult> handler =
                services.GetRequiredService<IQueryHandler<TQuery, TResult>>();

            NextDelegate<TResult> pipeline = () => handler.HandleAsync(typed, ct);

            foreach (IPipelineBehavior<TQuery, TResult> behavior in services
                .GetServices<IPipelineBehavior<TQuery, TResult>>()
                .Reverse())
            {
                NextDelegate<TResult> next = pipeline;
                pipeline = () => behavior.HandleAsync(typed, next, ct);
            }

            return pipeline();
        }
    }
}
