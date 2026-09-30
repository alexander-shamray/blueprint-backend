using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>§6.5's cursor codec: a lossless round trip, and null rather than a throw for a tampered token.</summary>
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
        DateTimeOffset local = new(2026, 8, 8, 14, 30, 15, TimeSpan.FromHours(2));

        (DateTimeOffset SortKey, Guid Id)? decoded = Cursor.Decode(Cursor.Encode(local, Guid.Empty));

        decoded.ShouldNotBeNull();
        decoded.Value.SortKey.ShouldBe(local);
        decoded.Value.SortKey.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Decode_returns_null_for_null()
    {
        // §6.5's first-page branch is this call.
        Cursor.Decode(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url !!!")]
    [InlineData("bm90LWEtY3Vyc29y")]                  // valid Base64Url, wrong payload
    [InlineData("OTk5OTk5OTk5OTk5OTk5OTk5OTk6YWJj")]  // ticks that overflow long entirely
    public void Decode_returns_null_for_anything_unreadable(string tampered)
    {
        // The cursor is opaque (ADR-016).
        Cursor.Decode(tampered).ShouldBeNull();
    }

    [Fact]
    public void Decode_returns_null_for_ticks_past_the_calendar()
    {
        // These ticks parse, so only the range guard stands between them and a constructor throw.
        string payload = string.Create(
            CultureInfo.InvariantCulture, $"{DateTime.MaxValue.Ticks + 1}:{Guid.Empty:N}");
        string cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));

        Cursor.Decode(cursor).ShouldBeNull();
    }
}
