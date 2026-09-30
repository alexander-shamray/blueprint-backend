using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§13.4's never-log vocabulary, pinned whole so that a deleted term fails here.</summary>
public class SensitiveKeysTests
{
    // Spelled out rather than read from SensitiveKeys.All, which could not notice it changing.
    private static readonly string[] Expected =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "authorization",
        "credential",
        "cookie",
        "apikey",
        "api_key",
        "connectionstring",
        "connection_string",
        "privatekey",
        "private_key",
        "cardnumber",
        "card_number",
        "ssn",
        "nationalid",
        "cvv",
        "otp",
        "sessionid",
        "session_id",
        "accountkey",
        "account_key",
        "signature"
    ];

    [Fact]
    public void The_vocabulary_is_the_one_that_was_decided()
    {
        SensitiveKeys.All.ShouldBe(Expected);
    }

    [Fact]
    public void The_vocabulary_cannot_be_rewritten_through_the_view()
    {
        // A backing array cast back would let a caller rewrite the list at run time.
        (SensitiveKeys.All as string[]).ShouldBeNull(
            "a caller that can cast this back can silently widen what the platform exports");
    }

    [Theory]
    [InlineData("ConnectionString")]
    [InlineData("Catalog__ConnectionString")]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("Pwd")]
    [InlineData("Passwd")]
    [InlineData("Set-Cookie")]
    [InlineData("PrivateKey")]
    [InlineData("Cvv")]
    [InlineData("SessionId")]
    [InlineData("AccountKey")]
    [InlineData("Signature")]
    public void A_key_this_codebase_actually_uses_is_matched(string key)
    {
        // Named cases, each a spelling from this repository's own vocabulary.
        SensitiveKeys.Matches(key).ShouldBeTrue(key);
    }

    [Theory]
    [InlineData("ShippingAddress")]
    [InlineData("RequestType")]
    [InlineData("CorrelationId")]
    [InlineData("OrderId")]
    [InlineData("Customer")]
    [InlineData("Currency")]
    public void An_innocent_key_is_not_matched(string key)
    {
        // An address is kept off the wire by §11.7 rather than out of the log.
        SensitiveKeys.Matches(key).ShouldBeFalse(key);
    }

    [Theory]
    [InlineData("Server=sql,1433;Database=Catalog;User Id=sa;Password=hunter2;Encrypt=False")]
    [InlineData("Server=sql,1433;Database=Catalog;User Id=sa;Pwd=hunter2")]
    [InlineData("server=sql;user id=sa;PASSWORD=hunter2")]
    // ADO.NET tolerates whitespace around the separator, which a literal "password=" check misses.
    [InlineData("Server=sql;Database=Catalog;User Id=sa;Password = hunter2")]
    [InlineData("Server=sql;Password	=hunter2")]
    [InlineData("Server=sql;Pwd = hunter2")]
    [InlineData("Server=sql;Password  =  hunter2")]
    public void A_connection_string_is_matched_by_its_value(string value)
    {
        // A diagnostic can name no sensitive key at all, so the value is checked too.
        SensitiveKeys.LooksLikeSecret(value).ShouldBeTrue();
    }

    [Fact]
    public void A_jwt_is_matched_by_its_value()
    {
        SensitiveKeys.LooksLikeSecret("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln").ShouldBeTrue();
    }

    [Theory]
    [InlineData("the password was rejected")]      // names it, assigns nothing
    [InlineData("password")]
    [InlineData("Walnut desk")]
    [InlineData("018f4c2e-0000-7000-8000-000000000000")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9")]                       // a prefix, but no dots
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0")]       // one dot, not two
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_value_is_not_matched(string? value)
    {
        // The control: a value check matching everything would pass the tests above.
        SensitiveKeys.LooksLikeSecret(value).ShouldBeFalse();
    }

    [Fact]
    public void A_non_string_value_is_not_matched()
    {
        // Guards the cast: OnEnd hands this whatever was bound, and an int has no shape to recognise.
        SensitiveKeys.LooksLikeSecret(42).ShouldBeFalse();
    }
}
