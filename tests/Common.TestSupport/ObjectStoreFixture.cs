using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Common.TestSupport;

/// <summary>ADR-106's local store as §14.1 runs it: the same image, identities, bucket rule and edge.</summary>
/// <remarks>
/// Every file under <c>deploy/compose/object-store</c> is mounted rather than copied (§14.1), so a suite that
/// passes here passes against what <c>docker compose up</c> starts; the one-shot's commands run in the store.
/// </remarks>
public sealed class ObjectStoreFixture : IAsyncLifetime
{
    /// <summary>The bucket <c>init.txt</c> creates, and the one the identities in <c>s3.json</c> are scoped
    /// to.</summary>
    public const string Bucket = "product-images";

    private const int S3Port = 8333;

    private const int EdgePort = 8080;

    private static readonly string Directory = Path.Combine(
        ComposeImage.RepositoryRoot(),
        "deploy",
        "compose",
        "object-store");

    private INetwork? _network;

    private IContainer? _store;

    private IContainer? _edge;

    /// <summary>The S3 endpoint as the test host reaches it, which is what a client would be signed for.</summary>
    public Uri Endpoint => new($"http://127.0.0.1:{_store!.GetMappedPublicPort(S3Port)}");

    /// <summary>Where the public image is served, as the test host reaches it: the edge in front of the
    /// store.</summary>
    public Uri PublicBase => new($"http://127.0.0.1:{_edge!.GetMappedPublicPort(EdgePort)}");

    /// <summary>Runs <paramref name="command"/> in the store's container, where the one-shot's script runs.</summary>
    public Task<ExecResult> ExecInStoreAsync(IList<string> command, CancellationToken ct) =>
        _store!.ExecAsync(command, ct);

    /// <summary>The key pair <c>s3.json</c> gives <paramref name="identity"/>, read so a test cannot restate
    /// it.</summary>
    public static (string AccessKey, string SecretKey) Credentials(string identity)
    {
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "s3.json")));

        JsonElement pair = config.RootElement
            .GetProperty("identities")
            .EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == identity)
            .GetProperty("credentials")[0];

        return (pair.GetProperty("accessKey").GetString()!, pair.GetProperty("secretKey").GetString()!);
    }

    public async ValueTask InitializeAsync()
    {
        TestcontainersWarmUp.Ensure();

        // One network, so the edge reaches the store by the name edge.conf proxies to.
        _network = new NetworkBuilder().Build();

        _store = new ContainerBuilder()
            .WithImage(ComposeImage.Of("object-store"))
            .WithNetwork(_network)
            .WithNetworkAliases("object-store")
            .WithCommand("server", "-s3", "-s3.config=/etc/seaweedfs/s3.json", "-dir=/data")
            .WithBindMount(Path.Combine(Directory, "s3.json"), "/etc/seaweedfs/s3.json", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(Directory, "init.sh"), "/etc/seaweedfs/init.sh", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(Directory, "init.txt"), "/etc/seaweedfs/init.txt", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(Directory, "policies"), "/etc/seaweedfs/policies", AccessMode.ReadOnly)
            .WithPortBinding(S3Port, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/status")))
            .Build();

        _edge = new ContainerBuilder()
            .WithImage(ComposeImage.Of("image-edge"))
            .WithNetwork(_network)
            .WithBindMount(Path.Combine(Directory, "edge.conf"), "/etc/nginx/conf.d/default.conf", AccessMode.ReadOnly)
            .WithPortBinding(EdgePort, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(EdgePort).ForPath("/healthz")))
            .Build();

        await _network.CreateAsync(TestContext.Current.CancellationToken);
        await DaemonRetry.StartAsync(_store, TestContext.Current.CancellationToken);
        await DaemonRetry.StartAsync(_edge, TestContext.Current.CancellationToken);

        // The one-shot's own script (the bucket, the policies and uploads/'s one-day rule), here in the store's own
        // container, and then again, as a second `up` runs it against the bucket the first made.
        foreach (string pass in (string[])["first", "second"])
        {
            ExecResult init = await _store.ExecAsync(
                ["/bin/sh", "/etc/seaweedfs/init.sh", "127.0.0.1:9333"],
                TestContext.Current.CancellationToken);

            if (init.ExitCode != 0)
                throw new InvalidOperationException(
                    $"The store's init script failed on its {pass} run ({init.ExitCode}): {init.Stdout}{init.Stderr}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_edge is not null)
            await _edge.DisposeAsync();
        if (_store is not null)
            await _store.DisposeAsync();
        if (_network is not null)
            await _network.DisposeAsync();
    }
}
