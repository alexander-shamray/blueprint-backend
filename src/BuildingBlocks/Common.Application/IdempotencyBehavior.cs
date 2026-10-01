using System.Reflection;
using System.Text.Json;

namespace Common.Application;

/// <summary>§8.5's claim-before-work exclusion, between validation and the transaction (§6.3).</summary>
/// <remarks>A command that does not opt in, or returns no <see cref="Result"/>, is unprotected (§8.5).</remarks>
public sealed class IdempotencyBehavior<TCommand, TResult>(
    IIdempotencyStore store,
    ICurrentUser currentUser,
    IdempotencyContext idempotency)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>, IIdempotentCommand
    where TResult : Result
{
    private static readonly TimeSpan Retention = IdempotencyRetention.Window;

    // "null", not the empty string, which a store is likeliest to read as an absent payload.
    private const string NoValue = "null";

    // Opens a payload that carries a fingerprint; no JSON value begins with "s", so no bare value spells it.
    private const string FingerprintPrefix = "sha256:";

    // Resolved once per closed (TCommand, TResult), in declaration order; Result and Result<T> are the only shapes.
    private static readonly Type? ValueType = ValueTypeOf();

    private static readonly PropertyInfo? ValueProperty =
        ValueType is null ? null : typeof(TResult).GetProperty(nameof(Result<object>.Value));

    private static readonly MethodInfo? SuccessOfValue = ValueType is null
        ? null
        : typeof(Result)
            .GetMethod(nameof(Result.Success), 1, [Type.MakeGenericMethodParameter(0)])!
            .MakeGenericMethod(ValueType);

    public async Task<TResult> HandleAsync(TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // The store owns the prefix; the subject segment stops one caller naming another's key (§8.5).
        string key = $"{Subject()}:{TCommand.OperationName}:{command.CommandId}";

        // Before the claim, so a command that cannot be serialised holds no key (ADR-057).
        string fingerprint = CommandFingerprint.Of(command);

        // The token makes a write from an expired claim a no-op rather than a clobber of its successor's.
        string? claim = await store.TryClaimAsync(key, Retention, ct);

        if (claim is null)
        {
            IdempotencyEntry? existing = await store.GetAsync(key, ct);

            if (existing is null || existing.InProgress)
                throw new ConcurrentRequestException(command.CommandId);

            return Replay(existing.Payload!, fingerprint, command.CommandId);
        }

        // Set after the claim, so a command about to replay hands §6.3 no key.
        idempotency.Claim(key);

        TResult result;

        try
        {
            result = await next();
        }
        catch
        {
            // Released even if the commit landed unacknowledged: the retry then meets §6.3's marker.
            await store.ReleaseAsync(key, claim, CancellationToken.None);
            throw;
        }
        finally
        {
            // A scope may dispatch twice, so the key lives for this dispatch only.
            idempotency.Clear();
        }

        if (result.IsFailure)
        {
            // A refusal commits nothing, so there is nothing to replay.
            await store.ReleaseAsync(key, claim, CancellationToken.None);
            return result;
        }

        // No retention: the claim's window runs from the claim, not the commit (ADR-038).
        await store.CompleteAsync(key, claim, Capture(result, fingerprint), CancellationToken.None);
        return result;
    }

    // Bound from the principal (§11.4); every unauthenticated caller shares one segment, a residual §8.5 argues.
    private string Subject() => currentUser.IsAuthenticated ? currentUser.Id.ToString() : "system";

    // Only a success's value is stored, since a Result survives no JSON round trip (§8.5).
    private static string Capture(TResult result, string fingerprint)
    {
        string value = ValueType is null
            ? NoValue
            : JsonSerializer.Serialize(ValueProperty!.GetValue(result), ValueType);

        return Envelope(fingerprint) + value;
    }

    private static string Envelope(string fingerprint) => $"{FingerprintPrefix}{fingerprint}:";

    private static TResult Replay(string payload, string fingerprint, Guid commandId)
    {
        // Compared before any value is read; an unprefixed entry is the previous release's and replays (ADR-057).
        if (payload.StartsWith(FingerprintPrefix, StringComparison.Ordinal))
        {
            string envelope = Envelope(fingerprint);

            if (!payload.StartsWith(envelope, StringComparison.Ordinal))
                throw new CommandIdReusedException(commandId);

            payload = payload[envelope.Length..];
        }

        // The guard is required: the cast compiles for every TResult and fails at run time for all but Result.
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

        // Unreachable while Result<T> is sealed and Result's constructor is private protected.
        throw new NotSupportedException(
            $"{typeof(TResult).Name} is neither Result nor Result<T>, so no stored outcome " +
            "can be rebuilt for it. A third Result shape is a change to this behaviour.");
    }
}
