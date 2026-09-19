using Common.Application;

namespace Payments.Application.Orders.RecordOrderCancelled;

public sealed record RecordOrderCancelledCommand(Guid OrderId, DateTimeOffset CancelledAt) : ICommand<Result>;
