using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Common.Application;

/// <summary>§6.5's opaque cursor: the sort key plus the tiebreaker ID, Base64Url-encoded (ADR-016).</summary>
public static class Cursor
{
    public static string Encode(DateTimeOffset sortKey, Guid id) =>
        Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{sortKey.UtcTicks}:{id:N}")));

    /// <summary>Null for null and for anything unreadable, so an edited cursor gets the first page.</summary>
    public static (DateTimeOffset SortKey, Guid Id)? Decode(string? cursor)
    {
        // IsValid first: TryDecodeFromChars' Try covers only the destination
        // size and still throws FormatException on an invalid character.
        if (cursor is null || !Base64Url.IsValid(cursor))
            return null;

        string payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        string[] parts = payload.Split(':');
        if (parts.Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ||
            ticks > DateTime.MaxValue.Ticks ||
            !Guid.TryParseExact(parts[1], "N", out Guid id))
        {
            return null;
        }

        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }
}
