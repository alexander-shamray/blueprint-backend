using Notifications.TestSupport;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>A plain relay, one behind an untrusted certificate, and a Development host over the first.</summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    public Mailpit Plain { get; } = Mailpit.Plain();

    public Mailpit SelfSigned { get; } = Mailpit.SelfSigned();

    /// <summary>Shared by the rows that end in an answer; a fault row builds its own.</summary>
    public NotificationsWorkerFactory Host { get; private set; } = null!;

    /// <summary>A Development host over one sink, with a pipeline and so a breaker of its own.</summary>
    public static NotificationsWorkerFactory Development(
        Mailpit sink,
        string from = NotificationsWorkerFactory.LocalFrom) =>
        new(Unreachable.Sql, Unreachable.Rabbit, mailHost: sink.Host, mailPort: sink.Port, mailFrom: from);

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(Plain.StartAsync(ct), SelfSigned.StartAsync(ct));
        Host = Development(Plain);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Host?.Dispose();
        }
        finally
        {
            await Plain.DisposeAsync();
            await SelfSigned.DisposeAsync();
        }
    }
}

/// <summary>§12.4's per-assembly collection: one pair of relays, and a category every member class inherits.</summary>
[CollectionDefinition(nameof(MailpitCollection))]
[Trait("Category", "Integration")]
public sealed class MailpitCollection : ICollectionFixture<MailpitFixture>;
