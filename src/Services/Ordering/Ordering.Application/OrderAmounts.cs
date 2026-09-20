namespace Ordering.Application;

/// <summary>
/// The money this service can record: §7.2's money columns, which are the
/// saga's <c>Total</c> and every line price. Held once, so a handler that
/// refuses an amount refuses it on the column's own terms.
/// </summary>
public static class OrderAmounts
{
    public const int Precision = 19;
    public const int Scale = 4;

    /// <summary>
    /// The first amount a <c>decimal(Precision, Scale)</c> column cannot hold,
    /// multiplied out rather than written down so that it moves with
    /// <see cref="Precision"/> and <see cref="Scale"/> rather than agreeing
    /// with them by inspection.
    /// </summary>
    public static readonly decimal Ceiling = PowerOfTen(Precision - Scale);

    private static decimal PowerOfTen(int exponent)
    {
        decimal value = 1m;

        for (int index = 0; index < exponent; index++)
            value *= 10m;

        return value;
    }
}
