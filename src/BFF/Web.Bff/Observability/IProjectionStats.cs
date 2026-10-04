namespace Web.Bff.Observability;

/// <summary>The question <see cref="ProjectionMetrics"/>' gauge asks of <c>bff.Orders</c>.</summary>
public interface IProjectionStats
{
    /// <summary>Zero when every row has an owner.</summary>
    double UnattributedAgeSeconds();
}
