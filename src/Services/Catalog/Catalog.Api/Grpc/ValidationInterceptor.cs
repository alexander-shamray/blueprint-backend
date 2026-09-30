using FluentValidation;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Catalog.Api.Grpc;

/// <summary>
/// §10.5's 400 row in gRPC's vocabulary: a <see cref="ValidationException"/> (§6.3) left untranslated reaches
/// the BFF as <c>Unknown</c>, its 500, where <c>InvalidArgument</c> reports the caller's error.
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
            // gRPC's status carries one string, so each property name goes into the text rather than being dropped.
            string detail = string.Join(
                "; ",
                exception.Errors.Select(failure => $"{failure.PropertyName}: {failure.ErrorMessage}"));

            throw new RpcException(new Status(StatusCode.InvalidArgument, detail));
        }
    }
}
