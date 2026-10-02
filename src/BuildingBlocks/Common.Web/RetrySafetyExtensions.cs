using Common.Application;
using Microsoft.AspNetCore.Builder;

namespace Common.Web;

/// <summary>Why a repeat of a write endpoint's request is harmless, for an endpoint that is not keyed (§8.5).</summary>
public enum RetrySafety
{
    /// <summary>A repeat of the same request leaves the state the first left, and is answered as it was.</summary>
    Convergent,

    /// <summary>The endpoint writes nothing.</summary>
    ReadOnly
}

/// <summary>The kind <see cref="RetrySafetyExtensions.RetrySafe{TBuilder}"/> declared.</summary>
public sealed record RetrySafetyMetadata(RetrySafety Kind);

/// <summary>The <see cref="IIdempotentCommand"/> an endpoint builds from its request rather than binds.</summary>
public sealed record IdempotentCommandMetadata(Type Command);

/// <summary>The two declarations a write endpoint makes where its handler's signature cannot (ADR-058).</summary>
public static class RetrySafetyExtensions
{
    /// <summary>Declares that a repeat is harmless, and which kind of harmless.</summary>
    /// <remarks>Over any builder, since a gRPC service is declared on <c>MapGrpcService</c>'s own (§9.7).</remarks>
    public static TBuilder RetrySafe<TBuilder>(this TBuilder builder, RetrySafety kind)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RetrySafetyMetadata(kind));

    /// <summary>Declares the endpoint keyed by <typeparamref name="TCommand"/>, which its handler constructs.</summary>
    public static RouteHandlerBuilder Idempotent<TCommand>(this RouteHandlerBuilder builder)
        where TCommand : IIdempotentCommand =>
        builder.WithMetadata(new IdempotentCommandMetadata(typeof(TCommand)));
}
