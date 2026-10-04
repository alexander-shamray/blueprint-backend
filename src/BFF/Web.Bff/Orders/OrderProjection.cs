using System.Data;
using System.Text.Json;
using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Dapper;
using Web.Bff.Persistence;

namespace Web.Bff.Orders;

/// <summary>ADR-051's order row, kept from seven events by set-once columns, so arrival order never matters.</summary>
/// <remarks>
/// Public, because §6.2's scan is public-only. Each statement inserts a missing row (§10.7) and fills only null
/// columns, which is what makes the inbox's later, separate commit safe to repeat (§9.5).
/// </remarks>
public sealed class OrderProjection(
    IDbConnectionFactory connections,
    TimeProvider clock,
    ILogger<OrderProjection> log)
    : IIntegrationEventHandler<OrderPlaced>,
      IIntegrationEventHandler<OrderConfirmed>,
      IIntegrationEventHandler<OrderCancelled>,
      IIntegrationEventHandler<PaymentAuthorised>,
      IIntegrationEventHandler<PaymentRefunded>,
      IIntegrationEventHandler<ShipmentDispatched>,
      IIntegrationEventHandler<ShipmentDelivered>
{
    /// <summary>The lines, inserted once by whichever line-carrying event arrives first.</summary>
    private const string LinesSql =
        """
        -- UPDLOCK makes the two line-carrying events for one order wait for each other.
        IF NOT EXISTS (SELECT 1 FROM bff.OrderLines WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = @OrderId)
            INSERT INTO bff.OrderLines (OrderId, LineNumber, ProductId, Quantity, UnitPrice)
            SELECT @OrderId, l.LineNumber, l.ProductId, l.Quantity, l.UnitPrice
            FROM OPENJSON(@Lines)
                WITH (
                    LineNumber int '$.LineNumber',
                    ProductId uniqueidentifier '$.ProductId',
                    Quantity int '$.Quantity',
                    UnitPrice decimal(38, 10) '$.UnitPrice') AS l;
        """;

    /// <summary>The owner as the row now holds it, read after the commit for the mismatch check.</summary>
    private const string OwnerSql =
        """
        SELECT CustomerId
        FROM bff.Orders
        WHERE OrderId = @OrderId;
        """;

    private const string PlacedSql =
        $"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, CustomerId, Currency, TotalAmount, PlacedAt, FirstSeenAt, AsOf)
            VALUES (@OrderId, @CustomerId, @Currency, @TotalAmount, @OccurredAt, @Now, @Now)
        WHEN MATCHED AND target.PlacedAt IS NULL THEN
            UPDATE SET
                CustomerId  = COALESCE(target.CustomerId, @CustomerId),
                Currency    = COALESCE(target.Currency, @Currency),
                TotalAmount = COALESCE(target.TotalAmount, @TotalAmount),
                PlacedAt    = @OccurredAt,
                AsOf        = @Now;

        {LinesSql}

        COMMIT;

        {OwnerSql}
        """;

    private const string ConfirmedSql =
        $"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, CustomerId, Currency, TotalAmount, ConfirmedAt, FirstSeenAt, AsOf)
            VALUES (@OrderId, @CustomerId, @Currency, @TotalAmount, @OccurredAt, @Now, @Now)
        WHEN MATCHED AND target.ConfirmedAt IS NULL THEN
            UPDATE SET
                CustomerId  = COALESCE(target.CustomerId, @CustomerId),
                Currency    = COALESCE(target.Currency, @Currency),
                TotalAmount = COALESCE(target.TotalAmount, @TotalAmount),
                ConfirmedAt = @OccurredAt,
                AsOf        = @Now;

        {LinesSql}

        COMMIT;

        {OwnerSql}
        """;

    private const string CancelledSql =
        $"""
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, CustomerId, CancelledAt, CancelOutcome, FirstSeenAt, AsOf)
            VALUES (@OrderId, @CustomerId, @OccurredAt, @CancelOutcome, @Now, @Now)
        WHEN MATCHED AND target.CancelledAt IS NULL THEN
            UPDATE SET
                CustomerId    = COALESCE(target.CustomerId, @CustomerId),
                CancelledAt   = @OccurredAt,
                CancelOutcome = @CancelOutcome,
                AsOf          = @Now;

        {OwnerSql}
        """;

    private const string AuthorisedSql =
        """
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, PaymentCurrency, AuthorisedAt, AuthorisedAmount, FirstSeenAt, AsOf)
            VALUES (@OrderId, @PaymentCurrency, @OccurredAt, @Amount, @Now, @Now)
        WHEN MATCHED AND target.AuthorisedAt IS NULL THEN
            UPDATE SET
                PaymentCurrency  = COALESCE(target.PaymentCurrency, @PaymentCurrency),
                AuthorisedAt     = @OccurredAt,
                AuthorisedAmount = @Amount,
                AsOf             = @Now;
        """;

    private const string RefundedSql =
        """
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, PaymentCurrency, RefundedAt, RefundedAmount, FirstSeenAt, AsOf)
            VALUES (@OrderId, @PaymentCurrency, @OccurredAt, @Amount, @Now, @Now)
        WHEN MATCHED AND target.RefundedAt IS NULL THEN
            UPDATE SET
                PaymentCurrency = COALESCE(target.PaymentCurrency, @PaymentCurrency),
                RefundedAt      = @OccurredAt,
                RefundedAmount  = @Amount,
                AsOf            = @Now;
        """;

    private const string DispatchedSql =
        """
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, TrackingNumber, DispatchedAt, FirstSeenAt, AsOf)
            VALUES (@OrderId, @TrackingNumber, @OccurredAt, @Now, @Now)
        WHEN MATCHED AND target.DispatchedAt IS NULL THEN
            UPDATE SET
                TrackingNumber = COALESCE(target.TrackingNumber, @TrackingNumber),
                DispatchedAt   = @OccurredAt,
                AsOf           = @Now;
        """;

    private const string DeliveredSql =
        """
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, TrackingNumber, DeliveredAt, FirstSeenAt, AsOf)
            VALUES (@OrderId, @TrackingNumber, @OccurredAt, @Now, @Now)
        WHEN MATCHED AND target.DeliveredAt IS NULL THEN
            UPDATE SET
                TrackingNumber = COALESCE(target.TrackingNumber, @TrackingNumber),
                DeliveredAt    = @OccurredAt,
                AsOf           = @Now;
        """;

    // CA1848 (ADR-019). Ids only, as §13.4 allows.
    private static readonly Action<ILogger, Guid, Guid?, Guid, Exception?> CustomerMismatch =
        LoggerMessage.Define<Guid, Guid?, Guid>(
            LogLevel.Warning,
            new EventId(1, nameof(CustomerMismatch)),
            "Order {OrderId} keeps customer {KeptCustomerId}; an Ordering event named {EventCustomerId}.");

    private static readonly Action<ILogger, string, Guid, int, Exception?> ValueDropped =
        LoggerMessage.Define<string, Guid, int>(
            LogLevel.Warning,
            new EventId(2, nameof(ValueDropped)),
            "Dropped {Field} on order {OrderId}: longer than its column's {Width} characters.");

    public Task HandleAsync(OrderPlaced integrationEvent, CancellationToken ct)
    {
        // CK_Orders_Total holds the pair together, so a currency that cannot be stored takes its total with it.
        string? currency = CurrencyOf(integrationEvent.Currency, integrationEvent.OrderId);

        return AttributeAsync(
            PlacedSql,
            integrationEvent.OrderId,
            integrationEvent.CustomerId,
            new
            {
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                Currency = currency,
                TotalAmount = currency is null ? (decimal?)null : integrationEvent.TotalAmount,
                integrationEvent.OccurredAt,
                Now = clock.GetUtcNow(),
                Lines = LinesJson(integrationEvent.Lines.Select(l => (l.ProductId, l.Quantity, l.UnitPrice)))
            },
            ct);
    }

    public Task HandleAsync(OrderConfirmed integrationEvent, CancellationToken ct)
    {
        // CK_Orders_Total holds the pair together, so a currency that cannot be stored takes its total with it.
        string? currency = CurrencyOf(integrationEvent.Currency, integrationEvent.OrderId);

        return AttributeAsync(
            ConfirmedSql,
            integrationEvent.OrderId,
            integrationEvent.CustomerId,
            new
            {
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                Currency = currency,
                TotalAmount = currency is null ? (decimal?)null : integrationEvent.TotalAmount,
                integrationEvent.OccurredAt,
                Now = clock.GetUtcNow(),
                Lines = LinesJson(integrationEvent.Lines.Select(l => (l.ProductId, l.Quantity, l.UnitPrice)))
            },
            ct);
    }

    public Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct) =>
        AttributeAsync(
            CancelledSql,
            integrationEvent.OrderId,
            integrationEvent.CustomerId,
            new
            {
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                integrationEvent.OccurredAt,
                CancelOutcome = CancellationOutcome.Of(integrationEvent.Origin, integrationEvent.Reason),
                Now = clock.GetUtcNow()
            },
            ct);

    public Task HandleAsync(PaymentAuthorised integrationEvent, CancellationToken ct) =>
        PaymentAsync(
            AuthorisedSql,
            integrationEvent.OrderId,
            integrationEvent.Currency,
            integrationEvent.Amount,
            integrationEvent.OccurredAt,
            ct);

    public Task HandleAsync(PaymentRefunded integrationEvent, CancellationToken ct) =>
        PaymentAsync(
            RefundedSql,
            integrationEvent.OrderId,
            integrationEvent.Currency,
            integrationEvent.Amount,
            integrationEvent.OccurredAt,
            ct);

    public Task HandleAsync(ShipmentDispatched integrationEvent, CancellationToken ct) =>
        WriteAsync(
            DispatchedSql,
            new
            {
                integrationEvent.OrderId,
                TrackingNumber = TrackingOf(integrationEvent.TrackingNumber, integrationEvent.OrderId),
                integrationEvent.OccurredAt,
                Now = clock.GetUtcNow()
            },
            ct);

    public Task HandleAsync(ShipmentDelivered integrationEvent, CancellationToken ct) =>
        WriteAsync(
            DeliveredSql,
            new
            {
                integrationEvent.OrderId,
                TrackingNumber = TrackingOf(integrationEvent.TrackingNumber, integrationEvent.OrderId),
                integrationEvent.OccurredAt,
                Now = clock.GetUtcNow()
            },
            ct);

    /// <summary>An Ordering event's write, then the owner it left, which a disagreeing event never moves.</summary>
    private async Task AttributeAsync(
        string sql,
        Guid orderId,
        Guid customerId,
        object parameters,
        CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        Guid? kept = await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));

        // A publisher defect if it ever differs; moving the order would show it to the wrong buyer (§10.7, §11.4).
        if (kept != customerId)
            CustomerMismatch(log, orderId, kept, customerId, null);
    }

    /// <summary>A payment's amount, written only with the currency that labels it, as the schema requires.</summary>
    private Task PaymentAsync(
        string sql,
        Guid orderId,
        string currency,
        decimal amount,
        DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        // An amount whose currency cannot be stored would be a number nobody can render, so neither is written.
        string? paymentCurrency = CurrencyOf(currency, orderId);
        if (paymentCurrency is null)
            return Task.CompletedTask;

        return WriteAsync(
            sql,
            new
            {
                OrderId = orderId,
                PaymentCurrency = paymentCurrency,
                OccurredAt = occurredAt,
                Amount = amount,
                Now = clock.GetUtcNow()
            },
            ct);
    }

    private async Task WriteAsync(string sql, object parameters, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    private string? CurrencyOf(string? currency, Guid orderId) =>
        Fitting(currency, ProjectionLimits.CurrencyLength, "Currency", orderId);

    private string? TrackingOf(string? trackingNumber, Guid orderId) =>
        Fitting(trackingNumber, ProjectionLimits.TrackingNumberMaxLength, "TrackingNumber", orderId);

    /// <summary>Another service's text, kept only when it fits its column, so it never faults the endpoint.</summary>
    private string? Fitting(string? value, int width, string field, Guid orderId)
    {
        if (value is null || value.Length <= width)
            return value;

        ValueDropped(log, field, orderId, width, null);
        return null;
    }

    /// <summary>The lines as one parameter, numbered by their position in the event.</summary>
    private static string LinesJson(IEnumerable<(Guid ProductId, int Quantity, decimal UnitPrice)> lines) =>
        JsonSerializer.Serialize(lines.Select((line, index) => new
        {
            LineNumber = index + 1,
            line.ProductId,
            line.Quantity,
            line.UnitPrice
        }));
}
