using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What <see cref="ComposeImage"/> reads out of a baseline shaped like §14.1's.</summary>
public class ComposeImageTests
{
    private static readonly string[] Baseline =
    [
        "services:",
        "  sql:",
        "    environment:",
        "      ACCEPT_EULA: \"Y\"",
        "    # a comment between keys",
        "",
        "    image: engine:pinned",
        "",
        "  # a comment between services",
        "  cache:",
        "    image: cache:7",
        "  broker:",
        "    build:",
        "      context: broker",
        "volumes:",
        "  data:"
    ];

    [Theory]
    [InlineData("sql", "engine:pinned")]
    [InlineData("cache", "cache:7")]
    public void A_service_reads_the_image_under_its_own_key(string service, string image) =>
        ComposeImage.Of(service, Baseline).ShouldBe(image);

    [Theory]
    [InlineData("absent")]
    [InlineData("broker")]
    [InlineData("data")]
    public void A_service_with_no_image_of_its_own_is_refused_not_given_its_neighbours(string service) =>
        Should.Throw<InvalidOperationException>(() => ComposeImage.Of(service, Baseline))
            .Message.ShouldContain(service);

    [Theory]
    [InlineData("sql")]
    [InlineData("redis-cache")]
    [InlineData("redis-coordination")]
    public void The_shipped_baseline_names_an_image_for_every_service_a_fixture_starts(string service) =>
        ComposeImage.Of(service).ShouldNotBeNullOrWhiteSpace();
}
