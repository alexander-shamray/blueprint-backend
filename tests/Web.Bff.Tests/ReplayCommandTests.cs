using BffReplay;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The rebuild's refusals, each made before anything is opened, so none needs a container.</summary>
public sealed class ReplayCommandTests
{
    [Fact]
    public async Task An_argument_other_than_reset_is_refused_before_the_environment_is_read()
    {
        int reads = 0;
        StringWriter error = new();

        int code = await ReplayCommand.RunAsync(
            ["--rest"],
            _ =>
            {
                reads++;
                return "set";
            },
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        code.ShouldBe(ReplayCommand.Refused);
        error.ToString().ShouldContain("Usage: bff-replay [--reset]");
        reads.ShouldBe(0);
    }

    [Fact]
    public async Task Reset_beside_a_second_argument_is_refused_too()
    {
        StringWriter error = new();

        int code = await ReplayCommand.RunAsync(
            [ReplayCommand.ResetFlag, "now"],
            _ => "set",
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        code.ShouldBe(ReplayCommand.Refused);
        error.ToString().ShouldContain("Usage: bff-replay [--reset]");
    }

    [Fact]
    public async Task Every_missing_connection_is_named_in_one_refusal()
    {
        StringWriter error = new();

        int code = await ReplayCommand.RunAsync(
            [ReplayCommand.ResetFlag],
            _ => null,
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        code.ShouldBe(ReplayCommand.Refused);
        foreach (string key in ReplaySettings.Keys)
            error.ToString().ShouldContain(key);
        error.ToString().ShouldContain("Nothing was read, deleted or sent");
    }

    [Fact]
    public async Task A_blank_connection_is_missing_rather_than_a_string_to_dial()
    {
        Dictionary<string, string> given = ReplaySettings.Keys.ToDictionary(key => key, _ => "Server=somewhere");
        given[ReplaySettings.BrokerKey] = "   ";
        StringWriter error = new();

        int code = await ReplayCommand.RunAsync(
            [],
            key => given[key],
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        code.ShouldBe(ReplayCommand.Refused);
        error.ToString().ShouldContain(ReplaySettings.BrokerKey);
        error.ToString().ShouldNotContain(ReplaySettings.BffKey);
    }

    [Fact]
    public void Every_key_given_reads_into_one_connection_per_publisher()
    {
        ReplaySettings settings = ReplaySettings.FromEnvironment(key => $"value-of-{key}");

        settings.Bff.ShouldBe($"value-of-{ReplaySettings.BffKey}");
        settings.Broker.ShouldBe($"value-of-{ReplaySettings.BrokerKey}");
        settings.Publishers.Select(p => p.Publisher).ShouldBe(Publisher.All);
        settings.Publishers.ShouldAllBe(p => p.ConnectionString == $"value-of-{p.Publisher.Key}");
    }
}
