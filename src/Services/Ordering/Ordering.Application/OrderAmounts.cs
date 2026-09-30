namespace Ordering.Application;

/// <summary>The money this service can record, on §7.2's money columns, held once.</summary>
public static class OrderAmounts
{
    public const int Precision = 19;
    public const int Scale = 4;

    /// <summary>The first amount the column cannot hold, computed so it moves with <see cref="Precision"/>.</summary>
    public static readonly decimal Ceiling = PowerOfTen(Precision - Scale);

    private static decimal PowerOfTen(int exponent)
    {
        decimal value = 1m;

        for (int index = 0; index < exponent; index++)
            value *= 10m;

        return value;
    }
}
