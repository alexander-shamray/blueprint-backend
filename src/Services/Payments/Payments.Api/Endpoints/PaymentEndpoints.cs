using Common.Application;
using Payments.Application.Admin.GetPayment;

namespace Payments.Api.Endpoints;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app
            .MapGroup("/v1/payments")
            .WithTags("Payments")
            .RequireAuthorization(PaymentsPermissions.Admin)
            .MapGet(
                "/{orderId:guid}",
                async (Guid orderId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    PaymentView? payment = await dispatcher.QueryAsync(new GetPaymentQuery(orderId), ct);

                    return payment is null ? Results.NotFound() : Results.Ok(payment);
                })
            .WithName("GetPayment");
    }
}
