using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>
/// The codec behind §6.5's opaque cursor. The contract has two halves: a
/// round-trip that loses nothing, and a decode that answers null — never a
/// throw — for anything a client may have done to the token.
/// </summary>
public class CursorTests
{
    [Fact]
    public void Encode_then_decode_returns_the_sort_key_and_the_tiebreaker()
    {
        DateTimeOffset sortKey = new(2026, 8, 8, 12, 30, 15, TimeSpan.Zero);
        Guid id = Guid.CreateVersion7();

        (DateTimeOffset SortKey, Guid Id)? decoded = Cursor.Decode(Cursor.Encode(sortKey, id));

        decoded.ShouldNotBeNull();
        decoded.Value.SortKey.ShouldBe(sortKey);
        decoded.Value.Id.ShouldBe(id);
    }

    [Fact]
    public void Encode_normalises_the_sort_key_to_utc()
    {
        // The token carries UtcTicks, so two encodings of the same instant in
        // different offsets are the same cursor — the seek predicate compares
        // instants, and an offset surviving the round trip would make the
        // page boundary depend on the client's time zone.
        DateTimeOffset local = new(2026, 8, 8, 14, 30, 15, TimeSpan.FromHours(2));

        (DateTimeOffset SortKey, Guid Id)? decoded = Cursor.Decode(Cursor.Encode(local, Guid.Empty));

        decoded.ShouldNotBeNull();
        decoded.Value.SortKey.ShouldBe(local);
        decoded.Value.SortKey.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Decode_returns_null_for_null()
    {
        // A first request has no cursor, and §6.5's handler leans on that:
        // Cursor.Decode(query.Cursor) is the whole of its first-page branch.
        Cursor.Decode(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url !!!")]
    [InlineData("bm90LWEtY3Vyc29y")]                  // valid Base64Url, wrong payload
    [InlineData("OTk5OTk5OTk5OTk5OTk5OTk5OTk6YWJj")]  // ticks that overflow long entirely
    public void Decode_returns_null_for_anything_unreadable(string tampered)
    {
        // The cursor is opaque (ADR-016). A client that edits one gets the
        // first page, not an error oracle over the token's insides and not a
        // 500 that makes garbage input this service's fault.
        Cursor.Decode(tampered).ShouldBeNull();
    }

    [Fact]
    public void Decode_returns_null_for_ticks_past_the_calendar()
    {
        // Distinct from the overflow row above, which long.TryParse already
        // refuses: these ticks parse and the id is valid, so only the range
        // guard itself stands between them and a DateTimeOffset constructor
        // throw.
        string payload = string.Create(
            CultureInfo.InvariantCulture, $"{DateTime.MaxValue.Ticks + 1}:{Guid.Empty:N}");
        string cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));

        Cursor.Decode(cursor).ShouldBeNull();
    }
}
