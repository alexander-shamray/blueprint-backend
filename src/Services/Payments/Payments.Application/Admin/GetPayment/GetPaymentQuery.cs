using Common.Application;

namespace Payments.Application.Admin.GetPayment;

public sealed record GetPaymentQuery(Guid OrderId) : IQuery<PaymentView?>;
