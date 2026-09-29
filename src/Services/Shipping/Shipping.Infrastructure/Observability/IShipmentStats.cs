namespace Shipping.Infrastructure.Observability;

/// <summary>
/// The one question spec section 11's waiting gauge asks: how many shipments in
/// each state are past their first failed pass.
/// </summary>
public interface IShipmentStats
{
    int WaitingCount(string state);
}
