namespace Shipping.Worker.Tests;

/// <summary>Infrastructure a host can name and never reach (§12.4), for suites driving one outbound adapter.</summary>
internal static class Unreachable
{
    // tcp and a one-second bound, since a bare name falls back to named pipes and fails only at the provider's timeout.
    public const string Sql =
        "Server=tcp:sql.invalid,1433;Database=Shipping;User Id=x;Password=x;" +
        "TrustServerCertificate=true;Connect Timeout=1";

    public const string Rabbit = "amqp://shipping-svc:x@rabbit.invalid:5672";
}
