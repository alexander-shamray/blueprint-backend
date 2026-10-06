using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>The table holding this service's personal data (ADR-052), against a real engine's columns.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class DeliveryAddressStoreTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>A Kazakh-script address, in letters a Cyrillic code page would lose.</summary>
    private static readonly DeliveryAddress Kazakh =
        new("Абай даңғылы 1, ә ғ қ ң ө ұ ү һ і", "пәтер 12", "Алматы", "050000", "KZ");

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IDeliveryAddressStore Store(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDeliveryAddressStore>();

    [Fact]
    public async Task A_kazakh_address_round_trips_through_the_table()
    {
        OrderId order = new(Guid.CreateVersion7());

        await SaveAsync(order, Guid.CreateVersion7(), Kazakh);

        (await ReadAsync(order)).ShouldBe(Kazakh);
    }

    [Fact]
    public async Task A_second_save_for_the_same_order_leaves_one_row_holding_the_later_address()
    {
        OrderId order = new(Guid.CreateVersion7());
        Guid firstCustomer = Guid.CreateVersion7();
        Guid laterCustomer = Guid.CreateVersion7();
        DeliveryAddress later = Kazakh with { Line2 = "пәтер 13" };
        DateTimeOffset laterInstant = Now.AddMinutes(1);
        await SaveAsync(order, firstCustomer, Kazakh);

        // Idempotent per order: a second save leaves one row answering it in full, customer included.
        await SaveAsync(order, laterCustomer, later, laterInstant);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.DeliveryAddresses WHERE OrderId = {0}",
            order.Value))
            .ShouldBe(1);
        (await ReadAsync(order)).ShouldBe(later);
        (await fixture.ScalarAsync<Guid>(
            "SELECT Value = CustomerId FROM shipping.DeliveryAddresses WHERE OrderId = {0}",
            order.Value))
            .ShouldBe(laterCustomer);
        (await fixture.ScalarAsync<DateTimeOffset>(
            "SELECT Value = FetchedAt FROM shipping.DeliveryAddresses WHERE OrderId = {0}",
            order.Value))
            .ShouldBe(laterInstant);
    }

    [Fact]
    public async Task An_absent_second_line_reads_back_as_absent_rather_than_blank()
    {
        OrderId order = new(Guid.CreateVersion7());
        DeliveryAddress single = Kazakh with { Line2 = null };

        await SaveAsync(order, Guid.CreateVersion7(), single);

        DeliveryAddress? saved = await ReadAsync(order);
        saved.ShouldNotBeNull();
        saved.Line2.ShouldBeNull();
    }

    [Fact]
    public async Task An_order_with_no_row_reads_as_null()
    {
        (await ReadAsync(new OrderId(Guid.CreateVersion7()))).ShouldBeNull();
    }

    [Fact]
    public async Task The_customer_is_stored_beside_the_address_and_erasure_deletes_by_it()
    {
        OrderId order = new(Guid.CreateVersion7());
        Guid customer = Guid.CreateVersion7();
        await SaveAsync(order, customer, Kazakh);

        // §11.7's erasure statement, named beside the table as ADR-052 asks.
        await fixture.ExecuteAsync("DELETE FROM shipping.DeliveryAddresses WHERE CustomerId = {0};", customer);

        (await ReadAsync(order)).ShouldBeNull();
    }

    [Fact]
    public async Task The_columns_are_the_ones_section_7_names()
    {
        string[] columns = await fixture.ColumnsAsync("shipping", "DeliveryAddresses");

        columns.ShouldBe(
            ["OrderId", "CustomerId", "Line1", "Line2", "City", "PostalCode", "Country", "FetchedAt"],
            ignoreOrder: true,
            "the shipment's own record holds no personal data, so a column added here is a decision");

        // Country alone is fixed-width and non-Unicode, where the model's default would have been wrong.
        (await ColumnShapeAsync("Country")).ShouldBe("char(2)");
        (await ColumnShapeAsync("Line1")).ShouldBe("nvarchar(200)");
        (await ColumnShapeAsync("Line2")).ShouldBe("nvarchar(200) NULL");
        (await ColumnShapeAsync("City")).ShouldBe("nvarchar(100)");
        (await ColumnShapeAsync("PostalCode")).ShouldBe("nvarchar(32)");
    }

    private Task SaveAsync(OrderId order, Guid customer, DeliveryAddress address) =>
        SaveAsync(order, customer, address, Now);

    private async Task SaveAsync(OrderId order, Guid customer, DeliveryAddress address, DateTimeOffset fetchedAt)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        await Store(scope).SaveAsync(order, customer, address, fetchedAt, TestContext.Current.CancellationToken);
    }

    private Task<string> ColumnShapeAsync(string column) =>
        fixture.ScalarAsync<string>(
            """
            SELECT Value = CONCAT(DATA_TYPE, '(', CHARACTER_MAXIMUM_LENGTH, ')', IIF(IS_NULLABLE = 'YES', ' NULL', ''))
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'shipping' AND TABLE_NAME = 'DeliveryAddresses' AND COLUMN_NAME = {0}
            """,
            column);

    private async Task<DeliveryAddress?> ReadAsync(OrderId order)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await Store(scope).GetAsync(order, TestContext.Current.CancellationToken);
    }
}
