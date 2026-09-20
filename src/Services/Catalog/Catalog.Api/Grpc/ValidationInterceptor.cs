using FluentValidation;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Catalog.Api.Grpc;

/// <summary>
/// §10.5's 400 row in gRPC's vocabulary. <c>ValidationBehavior</c> throws a
/// <see cref="ValidationException"/> (§6.3); untranslated it reaches gRPC's
/// handler as <c>Unknown</c>, which the BFF leaves unmapped as a 500 — a
/// caller's bad request reported as this platform failing.
/// <c>InvalidArgument</c> is the code that says "you sent the wrong thing".
/// Not a retry concern: <c>Unknown</c> rides <c>grpc-status</c> on an HTTP
/// 200, so the BFF's resilience pipeline never sees it. An interceptor, not
/// a <c>try</c> in the service, because the rule belongs to every RPC.
/// </summary>
internal sealed class ValidationInterceptor : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (ValidationException exception)
        {
            // Property name and message, joined — gRPC's status carries one
            // string where problem+json carries a keyed dictionary, so the key
            // goes into the text rather than being dropped. A caller debugging
            // "which field" is the whole audience for this.
            string detail = string.Join(
                "; ",
                exception.Errors.Select(failure => $"{failure.PropertyName}: {failure.ErrorMessage}"));

            throw new RpcException(new Status(StatusCode.InvalidArgument, detail));
        }
    }
}
