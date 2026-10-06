using System.Reflection;
using Common.Application;
using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Application.Tests;

/// <summary>
/// §8.5's opt-in gate, because the container silently omits <c>IdempotencyBehavior</c> for a command that
/// carries a <c>CommandId</c> without <see cref="IIdempotentCommand"/>.
/// </summary>
public class IdempotencyOptInTests
{
    private static readonly Assembly Application = typeof(Inventory.Application.DependencyInjection).Assembly;

    [Fact]
    public void Commands_carrying_a_CommandId_declare_IIdempotentCommand()
    {
        IEnumerable<string> offenders = Commands()
            .Where(t => t.GetProperty("CommandId") is not null)
            .Where(t => !typeof(IIdempotentCommand).IsAssignableFrom(t))
            .Select(t => t.Name);

        offenders.ShouldBeEmpty(
            "a CommandId without IIdempotentCommand is a field that promises protection " +
            "the pipeline never applies (§6.4, §8.5)");
    }

    [Fact]
    public void The_gate_above_is_looking_at_this_service_s_commands()
    {
        // The gate-coverage rule: the offender list above is as green when the selector matches nothing.
        Commands().ShouldNotBeEmpty("Inventory declares commands; the selector above found none");
    }

    [Fact]
    public void Every_idempotent_command_declares_a_stable_operation_name()
    {
        // The compiler refuses a missing OperationName but not the type's own name, which a rename would change.
        foreach (Type command in Idempotent())
        {
            string name = OperationNameOf(command);

            name.ShouldNotBeNullOrWhiteSpace();
            name.ShouldNotBe(command.Name, $"{command.Name} keys on its own CLR name (§8.5)");
        }
    }

    [Fact]
    public void Idempotent_commands_return_a_result_shape_the_behaviour_rebuilds()
    {
        // Written to what the behaviour rebuilds, not to what the container's constraint admits.
        (Type Command, Type Result)[] candidates =
        [
            .. Idempotent()
                .SelectMany(t => t
                    .GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>))
                    .Select(i => (Command: t, Result: i.GetGenericArguments()[0])))
        ];

        // The gate's own subject, asserted first, since both checks below are green on an empty selection.
        candidates.ShouldNotBeEmpty(
            "no command in this assembly implements IIdempotentCommand, so this test is " +
            "looking at nothing — the interface has been renamed, moved, or not yet applied.");

        // Exactly the two shapes ValueTypeOf accepts.
        candidates
            .Where(pair => pair.Result != typeof(Result) &&
                !(pair.Result.IsGenericType && pair.Result.GetGenericTypeDefinition() == typeof(Result<>)))
            .Select(pair => $"{pair.Command.Name} -> {pair.Result.Name}")
            .ShouldBeEmpty(
                "IdempotencyBehavior is constrained to TResult : Result and rebuilds only Result " +
                "or Result<T>. The container silently omits an open generic whose constraints do " +
                "not hold (§6.3), and ValueTypeOf refuses any third shape — so a command opting " +
                "in with anything else is either never protected or fails at its first dispatch, " +
                "and nothing says so at build time or at startup.");

        candidates
            .Where(pair => pair.Result.IsGenericType)
            .Select(pair => (pair.Command, Value: pair.Result.GetGenericArguments()[0]))
            .Where(pair => pair.Value.Assembly.GetName().Name!.EndsWith(".Domain", StringComparison.Ordinal))
            .Select(pair => $"{pair.Command.Name} -> Result<{pair.Value.Name}>")
            .ShouldBeEmpty(
                "the success VALUE is stored serialised with default options and " +
                "no converters. A domain value object need not survive that round trip, and " +
                "nothing says so (§4.2) — an idempotent command returns a primitive, a Guid " +
                "or a DTO, never a domain value object.");
    }

    [Fact]
    public void Operation_names_are_distinct_within_this_service()
    {
        // OperationName is the key's middle segment, so two commands sharing one share a keyspace (§8.5).
        string[] names = [.. Idempotent().Select(OperationNameOf)];

        // With fewer than two idempotent commands the distinctness check cannot fail, so this fails on none.
        names.ShouldNotBeEmpty("Inventory declares an idempotent command; the selector above found none");

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            names.Length,
            "two commands sharing an OperationName share a keyspace, and a replay then " +
            "reconstructs the wrong result type (§8.5)");
    }

    [Fact]
    public void No_command_handler_dispatches_a_command()
    {
        // §8.5: a command dispatched from a command handler lands in its parent's open transaction. An integration
        // event handler may dispatch, since §9.5's InboxFilter opens no transaction before the consumer returns.
        // Reach is constructor parameters only.
        IEnumerable<string> offenders = CommandHandlers()
            .Where(t => t
                .GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == typeof(IDispatcher)))
            .Select(t => t.Name);

        offenders.ShouldBeEmpty(
            "a command handler that dispatches puts the inner command inside the outer " +
            "transaction, where §8.5's claim is completed against work that may still roll " +
            "back. Closing this means IdempotencyBehavior declining nested dispatches " +
            "outright, which is the paragraph in §8.5 that changes with it.");
    }

    [Fact]
    public void The_nested_dispatch_gate_is_looking_at_this_service_s_handlers()
    {
        // The anti-vacuity floor for the gate above, which depends on ICommandHandler<,>'s shape and assembly.
        CommandHandlers().ShouldNotBeEmpty(
            "Inventory declares command handlers; the selector above found none");
    }

    private static Type[] CommandHandlers() =>
    [
        .. Application
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)))
    ];

    private static string OperationNameOf(Type command) =>
        (string)command
            .GetProperty(nameof(IIdempotentCommand.OperationName), BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

    private static Type[] Idempotent() => [.. CommandFingerprintRule.IdempotentCommands(Application)];

    private static Type[] Commands() =>
    [
        .. Application
            .GetTypes()
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
    ];
}
