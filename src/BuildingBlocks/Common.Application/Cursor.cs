using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Common.Application;

/// <summary>§6.5's opaque cursor: the sort key plus the tiebreaker ID, Base64Url-encoded (ADR-016).</summary>
public static class Cursor
{
    public static string Encode(DateTimeOffset sortKey, Guid id) =>
        Wrap(string.Create(CultureInfo.InvariantCulture, $"{sortKey.UtcTicks}:{id:N}"));

    /// <summary>Null for null and for anything unreadable, so an edited cursor gets the first page.</summary>
    public static (DateTimeOffset SortKey, Guid Id)? Decode(string? cursor)
    {
        string[]? parts = Unwrap(cursor)?.Split(':');
        if (parts is not { Length: 2 } ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ||
            ticks > DateTime.MaxValue.Ticks ||
            !Guid.TryParseExact(parts[1], "N", out Guid id))
        {
            return null;
        }

        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }

    /// <summary>A payload a read lays out itself, made opaque as this cursor is (ADR-073).</summary>
    public static string Wrap(string payload) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));

    /// <summary>The payload <see cref="Wrap"/> made, or null for null and for anything that is not Base64Url.</summary>
    public static string? Unwrap(string? cursor)
    {
        // IsValid first: TryDecodeFromChars' Try covers only the destination
        // size and still throws FormatException on an invalid character.
        if (cursor is null || !Base64Url.IsValid(cursor))
            return null;

        return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
    }
}
