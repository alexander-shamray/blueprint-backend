using System.Reflection;
using Common.Application;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

/// <summary>
/// §8.5's opt-in gate. <c>IdempotencyBehavior</c> is constrained to
/// <see cref="IIdempotentCommand"/>, and the container omits an open-generic
/// registration whose constraints the closed type does not satisfy —
/// <b>silently</b>. So a command that carries a <c>CommandId</c> and forgets
/// the interface is dispatched unprotected, with no error and no warning, and
/// a retry runs the whole command a second time.
/// </summary>
/// <remarks>
/// The shape of the command is what is read, not the author's intent: a
/// <c>CommandId</c> member is a claim that retrying is safe, so it is the one
/// signal available that does not depend on remembering. The measurement
/// behind this gate is in <c>Common.Application.Tests</c> —
/// <c>A_command_that_does_not_opt_in_runs_unprotected_and_says_nothing</c> —
/// which establishes that nothing else reports the omission.
/// </remarks>
public class IdempotencyOptInTests
{
    private static readonly Assembly Application = typeof(Payments.Application.DependencyInjection).Assembly;

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
    public void The_opt_in_gate_is_looking_at_this_service_s_commands()
    {
        // The gate-coverage rule: an empty offender list above is the same
        // green whether every command opted in or the selector stopped
        // matching anything. This is the half that fails when ICommand<>
        // moves, Payments's namespaces are reorganised, or the assembly
        // anchor is renamed — none of which that assertion can see.
        Commands().ShouldNotBeEmpty();
    }

    [Fact]
    public void Every_idempotent_command_declares_a_stable_operation_name()
    {
        // #114. The key's middle segment must not be derivable from the type,
        // because a rename then changes a live key and a rolling deployment
        // serves both spellings at once. The compiler already refuses a
        // command that supplies no OperationName; what it cannot refuse is one
        // that supplies the type's own name back.
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
        // §8.5's gate, and it is written to what the BEHAVIOUR accepts rather
        // than to what the container's constraint accepts. An earlier revision
        // of this test asked `typeof(Result).IsAssignableFrom(result)`, which
        // is the constraint's own question — and §8.5 says in as many words
        // that a gate written that way "would pass a command the behaviour
        // cannot serve and leave it to fail on first use". The chapter
        // specified all three assertions below; this file implemented one.
        (Type Command, Type Result)[] candidates =
        [
            .. Commands()
                .Where(typeof(IIdempotentCommand).IsAssignableFrom)
                .SelectMany(t => t
                    .GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>))
                    .Select(i => (Command: t, Result: i.GetGenericArguments()[0])))
        ];

        // The gate's own subject, asserted before anything it found. Both
        // checks below are ShouldBeEmpty, which is green when the chain above
        // selected NOTHING — the one reason a gate must never pass.
        candidates.ShouldBeEmpty(
            "This service opts no command into idempotency yet, so the two shape checks below " +
            "are vacuous. The day it does, this test fails — restore the ShouldNotBeEmpty " +
            "form, which is what keeps a vacuous gate from quietly becoming a permanent one.");

        // Exactly the two shapes ValueTypeOf accepts, not every subtype of
        // Result. A third shape is unconstructible outside Common.Application
        // today — Result<T> is sealed and Result's constructor is private
        // protected — so this assertion is a floor against that changing
        // rather than a live catch, and it is the cheaper half.
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

        // The half with teeth. Result<Money> is constructible today, passes
        // every check above, and corrupts in silence on replay.
        candidates
            .Where(pair => pair.Result.IsGenericType)
            .Select(pair => (pair.Command, Value: pair.Result.GetGenericArguments()[0]))
            .Where(pair => pair.Value.Assembly.GetName().Name!.EndsWith(".Domain", StringComparison.Ordinal))
            .Select(pair => $"{pair.Command.Name} -> Result<{pair.Value.Name}>")
            .ShouldBeEmpty(
                "the stored payload is the success VALUE, serialised with default options and " +
                "no converters. Money has a private constructor, so it round-trips to a zero " +
                "amount and a null currency and nothing says so (§4.2) — an idempotent command " +
                "returns a primitive, a Guid or a DTO, never a domain value object.");
    }

    [Fact]
    public void Operation_names_are_distinct_within_this_service()
    {
        // OperationName is the middle segment of a Redis key whose other two
        // are the subject and the caller's CommandId, so two commands sharing
        // one collapse into a single keyspace: the same caller reusing a
        // CommandId across them is served the FIRST command's stored payload,
        // deserialised into the second's result type. The docs say "give it a
        // value the domain would recognise", which is precisely the advice
        // that makes a copied string plausible.
        string[] names = [.. Idempotent().Select(OperationNameOf)];

        // The gate-coverage floor, and it carries more weight here than
        // usual: with one idempotent command a distinctness check cannot
        // fail, so this assertion is the only part of the test that is
        // live today. It arms itself on the second command, which is the
        // moment the check is worth having — and until then it at least
        // fails when the selector stops finding anything.
        names.ShouldBeEmpty(
            "This service opts no command into idempotency yet, so the check below is "
            + "vacuous. The day it does, this test fails — replace it with the ShouldNotBeEmpty "
            + "form, which is what keeps a vacuous gate from quietly becoming a permanent one. "
            + "RESTORE AuthorizationPolicyTests IN THE SAME CHANGE: §8.5 requires an idempotent "
            + "command's endpoint to be authenticated, an anonymous one collapses every caller "
            + "into the shared system subject, and this service dropped that suite as a slice "
            + "file.");

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            names.Length,
            "two commands sharing an OperationName share a keyspace, and a replay then " +
            "reconstructs the wrong result type (§8.5)");
    }

    [Fact]
    public void No_command_handler_dispatches_a_command()
    {
        // §8.5 names one dispatch as outside every argument it makes. A
        // command sent from INSIDE a command handler lands in its parent's open
        // transaction, because §6.3 opens none when one is already active — so
        // this behaviour completes a claim for 24 hours against work the outer
        // transaction may still roll back, and a retry then replays a success
        // for a row that does not exist. The client cannot see that, which is
        // what makes it worse than the duplicate it replaces.
        //
        // The chapter calls the case "unreached rather than handled", and that
        // was a claim about this assembly with nothing checking it. A residual
        // nothing re-checks is a decision rather than a deferral: the day a
        // handler takes an IDispatcher the paragraph is silently false and the
        // hole is live.
        //
        // An IIntegrationEventHandler that dispatches is the near miss, and it
        // is deliberately NOT caught. §9.5's InboxFilter opens no IUnitOfWork
        // transaction — it adds its row on the DbContext after the consumer
        // returns — so HasActiveTransaction is false when that dispatch
        // arrives and §6.3 opens a transaction of its own. An entry point, not
        // a nested unit.
        //
        // Named by shape rather than by type, because this file is §4.5's
        // template: Ordering has such a handler and Payments does not, so a
        // type name here would reach every rendered service as a near miss
        // about something it has never had. SLICE_TOKEN would not catch it —
        // the name carries no slice token — so nothing else would.
        //
        // REACH: constructor parameters, which is where every handler in this
        // solution takes its dependencies. A handler that resolves
        // IServiceProvider and asks it for an IDispatcher is invisible here,
        // exactly as a forbidden-but-unused reference is invisible to §4.2's
        // gates. Late rather than absent.
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
        // The fourth anti-vacuity floor in this file. ShouldBeEmpty above is
        // green when the selector found nothing, and this one depends on
        // ICommandHandler<,> keeping both its shape and its assembly.
        CommandHandlers().ShouldNotBeEmpty();
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

    private static Type[] Idempotent() =>
        [.. Commands().Where(typeof(IIdempotentCommand).IsAssignableFrom)];

    private static Type[] Commands() =>
    [
        .. Application
            .GetTypes()
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
    ];
}
