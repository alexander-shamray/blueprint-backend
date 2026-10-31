using Common.TestSupport;
using DotNet.Testcontainers.Containers;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>
/// The store's one-shot against a master with no raft leader yet: the store reports healthy on its S3 listener first,
/// so the script's list can fail, and a bucket an earlier <c>up</c> made must not be sent to the create line.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ObjectStoreInitTests(ObjectStoreFixture store) : IClassFixture<ObjectStoreFixture>
{
    // A weed ahead of the real one on PATH that answers the first two lists as a master with no leader did on
    // 2026-10-31, an error line and exit 0, and hands everything else to the real one.
    private const string LeaderlessWeed = """
        mkdir -p /tmp/leaderless
        cat > /tmp/leaderless/weed <<'STUB'
        #!/bin/sh
        input=$(cat)
        if [ "$input" = s3.bucket.list ]; then
            n=$(cat /tmp/leaderless/lists 2>/dev/null || echo 0)
            echo $((n + 1)) > /tmp/leaderless/lists
            if [ "$n" -lt 2 ]; then
                echo 'Error: rpc error: code = Unknown desc = raft.Server: Not current leader'
                exit 0
            fi
        fi
        printf '%s\n' "$input" | /usr/bin/weed "$@"
        STUB
        chmod +x /tmp/leaderless/weed
        rm -f /tmp/leaderless/lists
        PATH=/tmp/leaderless:$PATH /bin/sh /etc/seaweedfs/init.sh 127.0.0.1:9333
        status=$?
        echo "lists: $(cat /tmp/leaderless/lists)"
        exit $status
        """;

    [Fact]
    public async Task A_kept_bucket_survives_a_master_with_no_leader_yet()
    {
        // The fixture has run the script twice already, so the bucket exists, as on a kept volume. The literal takes
        // this file's line endings, CRLF in a checkout, which sh reads as part of each command.
        ExecResult init = await store.ExecInStoreAsync(
            ["/bin/sh", "-c", LeaderlessWeed.ReplaceLineEndings("\n")],
            TestContext.Current.CancellationToken);

        // Read as absent, the failed list sent the create line to a store that already held the bucket.
        string output = $"{init.Stdout}{init.Stderr}";
        init.ExitCode.ShouldBe(0, output);
        output.ShouldNotContain("already exists");

        // The stand-in answered every list, so the script asked a third time rather than reading the first.
        init.Stdout.ShouldContain("lists: 3");
    }
}
