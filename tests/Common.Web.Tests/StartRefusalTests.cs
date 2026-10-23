using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>When <see cref="StartRefusal"/> asks a host again, and when it lets the answer through.</summary>
public class StartRefusalTests
{
    private sealed class Host : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static ObjectDisposedException Race() => new(nameof(IServiceProvider));

    [Fact]
    public void A_refusal_that_is_the_disposal_race_is_asked_again_until_the_host_says_why()
    {
        int starts = 0;
        InvalidOperationException reason = new("the pair is outside the vocabulary");

        Exception refusal = StartRefusal.Ask(() => new Host(), _ => throw (++starts < 3 ? Race() : reason));

        refusal.ShouldBeSameAs(reason);
        starts.ShouldBe(3);
    }

    [Fact]
    public void A_refusal_naming_another_disposed_object_is_returned_at_once()
    {
        int starts = 0;
        ObjectDisposedException other = new("HttpClient");

        Exception refusal = StartRefusal.Ask(() => new Host(), _ =>
        {
            starts++;
            throw other;
        });

        refusal.ShouldBeSameAs(other);
        starts.ShouldBe(1);
    }

    [Fact]
    public void A_host_that_loses_the_race_on_every_ask_fails_with_the_last_one_as_the_cause()
    {
        InvalidOperationException thrown = Should.Throw<InvalidOperationException>(
            () => StartRefusal.Ask(() => new Host(), _ => throw Race()));

        thrown.InnerException.ShouldBeOfType<ObjectDisposedException>();
    }

    [Fact]
    public void A_host_that_starts_is_not_a_refusal()
    {
        Should.Throw<InvalidOperationException>(() => StartRefusal.Ask(() => new Host(), _ => { }))
            .Message.ShouldContain("refused nothing");
    }

    [Fact]
    public async Task An_awaited_start_is_asked_again_past_the_race_and_returns_the_reason()
    {
        int starts = 0;
        InvalidOperationException reason = new("a duplicate persisted name");

        Exception refusal = await StartRefusal.AskAsync(
            () => new Host(),
            _ => ++starts < 3 ? Task.FromException(Race()) : Task.FromException(reason));

        refusal.ShouldBeSameAs(reason);
        starts.ShouldBe(3);
    }

    [Fact]
    public async Task An_awaited_host_that_loses_the_race_on_every_ask_fails_with_the_last_one_as_the_cause()
    {
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => StartRefusal.AskAsync(() => new Host(), _ => Task.FromException(Race())));

        thrown.InnerException.ShouldBeOfType<ObjectDisposedException>();
    }

    [Fact]
    public async Task Every_awaited_host_asked_is_disposed()
    {
        List<Host> hosts = [];

        await StartRefusal.AskAsync(
            () =>
            {
                Host host = new();
                hosts.Add(host);
                return host;
            },
            _ => Task.FromException(hosts.Count < 2 ? Race() : new InvalidOperationException("why")));

        hosts.Count.ShouldBe(2);
        hosts.ShouldAllBe(h => h.Disposed);
    }

    [Fact]
    public async Task An_awaited_start_that_succeeds_is_not_a_refusal()
    {
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => StartRefusal.AskAsync(() => new Host(), _ => Task.CompletedTask));

        thrown.Message.ShouldContain("refused nothing");
    }

    [Fact]
    public void Every_host_asked_is_disposed()
    {
        List<Host> hosts = [];

        StartRefusal.Ask(
            () =>
            {
                Host host = new();
                hosts.Add(host);
                return host;
            },
            _ => throw (hosts.Count < 2 ? Race() : new InvalidOperationException("why")));

        hosts.Count.ShouldBe(2);
        hosts.ShouldAllBe(h => h.Disposed);
    }
}
