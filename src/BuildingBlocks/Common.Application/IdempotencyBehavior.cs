using System.Reflection;
using System.Text.Json;

namespace Common.Application;

/// <summary>
/// §8.5's claim-before-work protection, seated between
/// <see cref="ValidationBehavior{TCommand,TResult}"/> and
/// <see cref="TransactionBehavior{TCommand,TResult}"/> (§6.3). What §8.5 buys
/// is at most one commit per key while the marker survives, and only part of
/// that is this type's doing: the claim below is the atomic exclusion that
/// makes a concurrent duplicate fail early, and the durable half is
/// <see cref="IIdempotencyMarkerStore"/>, written and read by §6.3 inside the
/// transaction ([ADR-037]).
/// </summary>
/// <remarks>
/// Constrained to <see cref="IIdempotentCommand"/>, so it fails open: the
/// container silently omits an open-generic registration whose constraints the
/// closed type does not satisfy, so a command that does not opt in is
/// dispatched with no protection and no diagnostic. That is the shape of an
/// unregistered handler (§6.2) and gets the same guard, a reflection test over
/// the command's shape.
/// <para>
/// <c>where TResult : Result</c> fails open the same way and is easier to
/// miss: a command returning anything else is unprotected too, and nothing
/// here can detect it, so a test per service pins that every idempotent
/// command returns a shape this behaviour can rebuild.
/// </para>
/// </remarks>
public sealed class IdempotencyBehavior<TCommand, TResult>(
    IIdempotencyStore store,
    ICurrentUser currentUser,
    IdempotencyContext idempotency)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>, IIdempotentCommand
    where TResult : Result
{
    /// <summary>
    /// How long a claim survives, from <see cref="IdempotencyRetention"/>.
    /// Every entry expires, completed and in progress alike, so a retry
    /// arriving after this claims a free key, and what stops it committing a
    /// second time is the durable marker §6.3 writes.
    /// <para>
    /// Passed once, on the claim, and never extended by the completion, so the
    /// window runs from <c>TryClaimAsync</c> whatever the handler does and the
    /// claim's start precedes the marker's stamp by construction. Which of the
    /// two expires first is not an ordering at all: §9.5's purge asks this
    /// store (ADR-039). What remains is §8.5's long-handler residual — the
    /// marker's INSERT has to commit inside this value, and nothing bounds a
    /// handler's runtime.
    /// </para>
    /// </summary>
    private static readonly TimeSpan Retention = IdempotencyRetention.Window;

    // "null" and not the empty string. IdempotencyEntry carries a Payload the
    // store has to tell apart from the in-progress marker it wrote on the
    // claim, and an empty string is the value an implementation is likeliest to
    // read as absent — which would replay every void-shaped command as
    // ConcurrentRequestException for a day. This is valid JSON and unambiguous.
    private const string NoValue = "null";

    // Result and Result<T> are the whole universe — Result's summary rules
    // out Unit and Result<void>, and its private protected constructor
    // confines a third shape to this assembly, where ValueTypeOf refuses it
    // — and the two members after this one depend on that. A static field
    // on a generic type has one instance per closed type, so all three are
    // resolved once per (TCommand, TResult) pair rather than once per
    // command, and they run in declaration order.
    private static readonly Type? ValueType = ValueTypeOf();

    private static readonly PropertyInfo? ValueProperty =
        ValueType is null ? null : typeof(TResult).GetProperty(nameof(Result<object>.Value));

    // Result.Success<T>, closed over that value type, and the factory rather
    // than the internal constructor because it is the type's stated
    // construction API (Result<T>'s own summary) — it guards nothing more.
    private static readonly MethodInfo? SuccessOfValue = ValueType is null
        ? null
        : typeof(Result)
            .GetMethod(nameof(Result.Success), 1, [Type.MakeGenericMethodParameter(0)])!
            .MakeGenericMethod(ValueType);

    public async Task<TResult> HandleAsync(TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // Key shape only — the store owns the service prefix and namespace.
        // The subject segment is not decoration: a key built from the command
        // and the client's value alone is entirely caller-controlled, so caller
        // A can name victim B's key and be handed B's result. Nor is the
        // operation segment free: it is declared on the command rather than
        // derived from the type name, so a rename cannot silently change it.
        string key = $"{Subject()}:{TCommand.OperationName}:{command.CommandId}";

        // The token names this attempt, and every write below carries it: a
        // claim that has expired under a long handler cannot complete or
        // release over its successor's, because the store compares before it
        // acts, and a lost claim is a no-op rather than a clobber.
        string? claim = await store.TryClaimAsync(key, Retention, ct);

        if (claim is null)
        {
            IdempotencyEntry? existing = await store.GetAsync(key, ct);

            if (existing is null || existing.InProgress)
                throw new ConcurrentRequestException(command.CommandId);

            return Replay(existing.Payload!);
        }

        // Handed to §6.3, which writes the durable marker under this key inside
        // the transaction and reads it back before anything runs. Set after the
        // claim rather than beside the key, so a command that lost the race and
        // is about to replay never hands a key to a transaction it will not
        // open.
        idempotency.Claim(key);

        TResult result;

        try
        {
            result = await next();
        }
        catch
        {
            // Release for a fault raised inside next(), and nowhere else.
            // §6.3's ExecuteAsync disposes the transaction on the way out,
            // which rolls it back for every fault this code can tell apart;
            // the one it cannot is the lost commit acknowledgement, where the
            // work is durable. Releasing is still right: the retry it admits
            // meets the marker §6.3 wrote in that transaction and is refused
            // with CommandAlreadyCommittedException, while holding would cost
            // the ordinary fault its retry and the held entry expires anyway.
            await store.ReleaseAsync(key, claim, CancellationToken.None);
            throw;
        }
        finally
        {
            // The key lives for exactly the dispatch that claimed it. A scope
            // is not promised to serve one command — an endpoint or an
            // integration-event handler may dispatch twice — and a key left
            // standing is captured by the next command's transaction, which
            // either refuses a command nobody protected or writes a marker
            // naming the wrong command's work.
            idempotency.Clear();
        }

        if (result.IsFailure)
        {
            // A refusal is rolled back by the same mechanism: ExecuteAsync
            // disposes an uncommitted transaction. So there is nothing worth
            // replaying, and holding the key would replay a refusal to the
            // caller who fixed their request and retried under the same key.
            await store.ReleaseAsync(key, claim, CancellationToken.None);
            return result;
        }

        // No retention here, on purpose: re-arming the entry at the commit,
        // after §6.3 stamped its marker inside the transaction, would let the
        // claim outlive the marker by the commit's tail. The store keeps what
        // the claim had left, so the outcome is replayable for the remainder
        // of that window rather than for a fresh one.
        await store.CompleteAsync(key, claim, Capture(result), CancellationToken.None);
        return result;
    }

    // The claim belongs to one subject, bound from the principal and never from
    // the command (§11.4). IsAuthenticated is false for both a message-borne
    // command and an anonymous HTTP request (its own summary), so this
    // segment is shared rather than unique — which is a residual, argued in
    // §8.5, not a detail. It cannot collide with an authenticated subject: the
    // alternative is a Guid rendered "D", and no Guid spells a word.
    private string Subject() => currentUser.IsAuthenticated ? currentUser.Id.ToString() : "system";

    // Only a success is ever stored, and what is stored is its value — never
    // the Result around it, which survives neither direction of a JSON round
    // trip (§8.5's trap callout).
    private static string Capture(TResult result) =>
        ValueType is null
            ? NoValue
            : JsonSerializer.Serialize(ValueProperty!.GetValue(result), ValueType);

    private static TResult Replay(string payload)
    {
        // (TResult)Result.Success() is legal C# under the constraint above and
        // throws InvalidCastException at run time for every TResult that is not
        // exactly Result — the compiler accepts it because Result is TResult's
        // effective base class, and the runtime refuses a base instance where a
        // derived one is required. The guard is what makes it safe, not an
        // optimisation, and removing it fails only at the first replay.
        if (ValueType is null)
            return (TResult)Result.Success();

        object? value = JsonSerializer.Deserialize(payload, ValueType);
        return (TResult)SuccessOfValue!.Invoke(null, [value])!;
    }

    private static Type? ValueTypeOf()
    {
        if (typeof(TResult) == typeof(Result))
            return null;

        if (typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(Result<>))
            return typeof(TResult).GetGenericArguments()[0];

        // Unreachable while Result<T> is sealed and Result's constructor is
        // private protected. Stated rather than assumed, though this runs from
        // a static field initialiser, so the CLR wraps it in a
        // TypeInitializationException either way; what it buys is an
        // InnerException naming the type and the reason, and moving the check
        // off the static path would cost it on every command instead of once
        // per closed generic.
        throw new NotSupportedException(
            $"{typeof(TResult).Name} is neither Result nor Result<T>, so no stored outcome " +
            "can be rebuilt for it. A third Result shape is a change to this behaviour.");
    }
}
