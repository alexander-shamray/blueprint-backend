namespace Shipping.Worker.Tests;

/// <summary>
/// Infrastructure a host can name and never reach (§12.4's <c>.invalid</c>
/// convention), for the suites that drive one outbound adapter and need the
/// rest of the host only to start.
/// </summary>
internal static class Unreachable
{
    // Named as tcp and bounded to a second, because a bare name falls back to
    // named pipes and fails only after the provider's own timeout: a host that
    // dials it has to fail inside one fulfilment tick (CarrierHop).
    public const string Sql =
        "Server=tcp:sql.invalid,1433;Database=Shipping;User Id=x;Password=x;" +
        "TrustServerCertificate=true;Connect Timeout=1";

    public const string Rabbit = "amqp://shipping-svc:x@rabbit.invalid:5672";
}
