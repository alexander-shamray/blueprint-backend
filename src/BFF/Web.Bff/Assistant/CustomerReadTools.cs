using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Catalog.Pricing.V1;
using Common.Application;
using Common.Assistant.Tools;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Web.Bff.Orders;

namespace Web.Bff.Assistant;

/// <summary>The customer assistant's read tools, each scoped to the caller the core passes (ADR-151).</summary>
/// <remarks>
/// <c>list_orders</c> and <c>get_order</c> read ADR-051's projection by the caller's subject, <c>get_product</c>
/// Catalog's pricing hop (§9.7); a refusal is a result the model reads, never an exception that ends the turn.
/// </remarks>
public sealed class CustomerReadTools(OrderReader orders, GrpcClientFactory clients) : IToolSource
{
    public const string ListOrders = "list_orders";

    public const string GetOrder = "get_order";

    public const string GetProduct = "get_product";

    /// <summary>The most recent orders <c>list_orders</c> returns; older ones are the web client's to page.</summary>
    public const int ListLimit = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web);

    private static readonly ToolAnnotations Reads = new(
        ReadOnlyHint: true,
        DestructiveHint: false,
        IdempotentHint: true,
        OpenWorldHint: false);

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(
            ListOrders,
            "List my orders",
            $"The signed-in customer's {ListLimit} most recent orders: each one's id, status, timeline, total and " +
            "lines.",
            Schema(new JsonObject()),
            Reads,
            ToolSide.Server),
        new(
            GetOrder,
            "Get one of my orders",
            "One of the signed-in customer's orders by id: its status, timeline, lines, payment and shipment, " +
            "including a tracking number once the carrier has one.",
            Schema(new JsonObject { ["order_id"] = Uuid("The order's id, as list_orders returns it.") }, "order_id"),
            Reads,
            ToolSide.Server),
        new(
            GetProduct,
            "Get a product",
            "A product's catalogue name and its price in one currency. Nothing comes back for a product that is " +
            "withdrawn, unknown, or not sold in that currency.",
            Schema(
                new JsonObject
                {
                    ["product_id"] = Uuid("The product's id, as an order line carries it."),
                    ["currency"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["pattern"] = "^[A-Za-z]{3}$",
                        ["description"] = "An ISO 4217 code, such as the currency of the customer's orders."
                    }
                },
                "product_id",
                "currency"),
            Reads,
            ToolSide.Server)
    ];

    public async ValueTask<ToolOutcome> InvokeAsync(
        string name,
        JsonObject input,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        // The core's caller is this host's subject, so a tool never reads another customer's rows (§10.7).
        if (!Guid.TryParse(context.Caller.Id, out Guid customer))
            return Refused("no_customer");

        try
        {
            return name switch
            {
                ListOrders => Found(await orders.ListAsync(customer, cursor: null, ListLimit, cancellationToken)),
                GetOrder => await OrderAsync(customer, input, cancellationToken),
                GetProduct => await ProductAsync(input, cancellationToken),
                _ => Refused("unknown_tool")
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A code, not the message the core would pass on: a database's text is no customer's to send (ADR-151).
            return Refused(e is RpcException ? "catalog_unavailable" : "orders_unavailable");
        }
    }

    private async Task<ToolOutcome> OrderAsync(Guid customer, JsonObject input, CancellationToken cancellationToken)
    {
        if (!TryGuid(input, "order_id", out Guid order))
            return Refused("order_id_not_a_uuid");

        Result<OrderDetail> detail = await orders.FindAsync(customer, order, cancellationToken);

        // Another customer's order answers as an unknown one does, as GetOrder's 404 (§10.7).
        return detail.IsSuccess ? Found(detail.Value) : Refused("order_not_found");
    }

    private async Task<ToolOutcome> ProductAsync(JsonObject input, CancellationToken cancellationToken)
    {
        if (!TryGuid(input, "product_id", out Guid product))
            return Refused("product_id_not_a_uuid");

        if (input["currency"] is not JsonValue value ||
            !value.TryGetValue(out string? currency) ||
            currency.Length != 3 ||
            !currency.All(char.IsAsciiLetter))
        {
            return Refused("currency_not_an_iso_code");
        }

        Pricing.PricingClient pricing = clients.CreateClient<Pricing.PricingClient>(PricingHop.ClientName);
        GetPricesRequest request = new() { Currency = currency.ToUpperInvariant() };
        request.ProductId.Add(product.ToString());

        GetPricesReply reply = await pricing.GetPricesAsync(request, cancellationToken: cancellationToken);

        // An empty reply is the one answer that is no fault: withdrawn, unknown, or not sold in this currency.
        if (reply.Price.Count == 0)
            return Refused("product_not_offered");

        // §9.7's rule 6, as the quote checks it: a reply that breaks pricing.proto is Catalog's fault, not a price.
        if (reply.Price is not [{ } price] ||
            !Guid.TryParse(price.ProductId, out Guid priced) || priced != product ||
            !string.Equals(price.Currency, request.Currency, StringComparison.OrdinalIgnoreCase) ||
            !decimal.TryParse(
                price.Amount,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal amount) ||
            amount < 0)
        {
            return Refused("catalog_unavailable");
        }

        return Found(new { ProductId = product, price.Name, Price = new { Amount = amount, price.Currency } });
    }

    private static bool TryGuid(JsonObject input, string key, out Guid value)
    {
        value = Guid.Empty;

        return input[key] is JsonValue node && node.TryGetValue(out string? text) && Guid.TryParse(text, out value);
    }

    private static ToolOutcome Found(object value) => new(true, JsonSerializer.Serialize(value, Json));

    private static ToolOutcome Refused(string code) => new(false, JsonSerializer.Serialize(new { Error = code }, Json));

    private static JsonObject Schema(JsonObject properties, params string[] required) =>
        new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
            ["additionalProperties"] = false
        };

    private static JsonObject Uuid(string description) =>
        new() { ["type"] = "string", ["format"] = "uuid", ["description"] = description };
}
