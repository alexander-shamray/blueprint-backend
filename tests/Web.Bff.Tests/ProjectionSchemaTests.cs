using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>What the database itself refuses and keeps, beneath the handlers that write it (ADR-051).</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class ProjectionSchemaTests(BffServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>SQL Server's constraint-violation error.</summary>
    private const int ConstraintViolation = 547;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_order_row_needs_only_its_key_and_the_two_instants()
    {
        // Any of the seven order events can create the row, so every fact column has to start empty.
        Guid orderId = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            orderId);

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.Orders WHERE OrderId = {0}", orderId))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_cancellation_without_its_member_is_refused()
    {
        SqlException refused = await Should.ThrowAsync<SqlException>(() => fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, CancelledAt, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            Guid.CreateVersion7()));

        refused.Number.ShouldBe(ConstraintViolation);
        refused.Message.ShouldContain("CK_Orders_Cancellation");
    }

    [Fact]
    public async Task A_member_outside_the_three_is_refused()
    {
        SqlException refused = await Should.ThrowAsync<SqlException>(() => fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, CancelledAt, CancelOutcome, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), N'refunded', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            Guid.CreateVersion7()));

        refused.Number.ShouldBe(ConstraintViolation);
        refused.Message.ShouldContain("CK_Orders_CancelOutcome");
    }

    [Fact]
    public async Task An_amount_without_its_payment_currency_is_refused()
    {
        SqlException refused = await Should.ThrowAsync<SqlException>(() => fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, AuthorisedAt, AuthorisedAmount, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), 59.97, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            Guid.CreateVersion7()));

        refused.Number.ShouldBe(ConstraintViolation);
        refused.Message.ShouldContain("CK_Orders_PaymentCurrency");
    }

    [Fact]
    public async Task A_payment_currency_with_no_amount_to_label_is_refused()
    {
        SqlException refused = await Should.ThrowAsync<SqlException>(() => fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, PaymentCurrency, FirstSeenAt, AsOf)
            VALUES ({0}, N'GBP', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            Guid.CreateVersion7()));

        refused.Number.ShouldBe(ConstraintViolation);
        refused.Message.ShouldContain("CK_Orders_PaymentCurrency");
    }

    [Fact]
    public async Task A_refund_recorded_before_any_authorisation_is_kept()
    {
        // §9.4 orders nothing, so the refund can arrive first; §10.7's detail route shows its half alone.
        Guid orderId = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, RefundedAt, RefundedAmount, PaymentCurrency, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), 59.97, N'GBP', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            orderId);

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.Orders WHERE OrderId = {0}", orderId))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_an_order_deletes_its_lines()
    {
        Guid orderId = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, FirstSeenAt, AsOf) VALUES ({0}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            INSERT INTO bff.OrderLines (OrderId, LineNumber, ProductId, Quantity, UnitPrice)
            VALUES ({0}, 0, NEWID(), 1, 9.99), ({0}, 1, NEWID(), 2, 4.50);
            DELETE FROM bff.Orders WHERE OrderId = {0};
            """,
            orderId);

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.OrderLines WHERE OrderId = {0}", orderId))
            .ShouldBe(0, "erasure deletes a buyer's orders and has to take their lines with them");
    }

    [Fact]
    public async Task The_owned_index_is_filtered_to_rows_with_an_owner()
    {
        string filter = await fixture.ScalarAsync<string>(
            """
            SELECT Value = filter_definition
            FROM sys.indexes
            WHERE name = 'IX_Orders_Owned' AND object_id = OBJECT_ID('bff.Orders')
            """);

        filter.ShouldBe("([CustomerId] IS NOT NULL)");
    }
}
