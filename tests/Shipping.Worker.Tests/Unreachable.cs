namespace Shipping.Worker.Tests;

/// <summary>
/// Infrastructure a host can name and never reach (§12.4's <c>.invalid</c>
/// convention), for the suites that drive one outbound adapter and need the
/// rest of the host only to start.
/// </summary>
internal static class Unreachable
{
    public const string Sql =
        "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true";

    public const string Rabbit = "amqp://shipping-svc:x@rabbit.invalid:5672";
}
