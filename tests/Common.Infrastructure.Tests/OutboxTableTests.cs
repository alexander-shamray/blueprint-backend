using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>A schema cannot be a SQL parameter, so the type refuses to hold one wrongly.</summary>
public class OutboxTableTests
{
    [Fact]
    public void The_table_is_schema_qualified_and_delimited()
    {
        new OutboxTable("catalog").QualifiedName.ShouldBe("[catalog].OutboxMessages");
    }

    [Fact]
    public void A_schema_that_is_a_reserved_word_still_parses()
    {
        // `user` is reserved, and brackets need no keyword list to keep current.
        new OutboxTable("user").QualifiedName.ShouldBe("[user].OutboxMessages");
    }

    [Fact]
    public void A_schema_longer_than_sysname_is_refused()
    {
        // 128 characters is what SQL Server's `sysname` holds.
        Should.NotThrow(() => new OutboxTable(new string('c', 128)));
        Should.Throw<ArgumentException>(() => new OutboxTable(new string('c', 129)));
    }

    [Theory]
    [InlineData("catalog; DROP TABLE catalog.Products --")]
    [InlineData("catalog.OutboxMessages")]
    [InlineData("[catalog]")]
    [InlineData("9catalog")]
    [InlineData("")]
    [InlineData(" catalog")]
    public void Anything_that_is_not_an_identifier_is_refused(string schema)
    {
        Should.Throw<ArgumentException>(() => new OutboxTable(schema));
    }
}
