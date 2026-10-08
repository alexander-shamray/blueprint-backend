using StackExchange.Redis;

namespace Common.Infrastructure.Tests;

/// <summary>§8.1's grant as §14.1's users.conf ships it, provisioned under a test's own user and keys.</summary>
internal static class DocumentedRedisGrant
{
    /// <summary>From the file both Compose instances load, so a test proves the grant that ships.</summary>
    public static IReadOnlyList<string> Rules() =>
        RulesOf(UserLines().Single(l => l.StartsWith("user catalog-svc ", StringComparison.Ordinal)));

    /// <summary>Every service user: the default user's line is the health checks' and grants PING alone.</summary>
    public static IEnumerable<string> UserLines() =>
        File.ReadLines(Path.Combine(RepositoryRoot(), "deploy", "compose", "redis", "users.conf"))
            .Where(l => l.StartsWith("user ", StringComparison.Ordinal) &&
                        !l.StartsWith("user default ", StringComparison.Ordinal));

    /// <summary>What follows the key pattern: the command rules, without the name, switch or password.</summary>
    public static IReadOnlyList<string> RulesOf(string line)
    {
        string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int pattern = Array.FindIndex(tokens, t => t.StartsWith('~'));
        return pattern < 0
            ? throw new InvalidOperationException($"A users.conf line has no key pattern: {line}")
            : tokens[(pattern + 1)..];
    }

    public static async Task ProvisionAsync(IConnectionMultiplexer admin, string user, string password, string keys)
    {
        object[] grant = ["SETUSER", user, "reset", "on", $">{password}", $"~{keys}", .. Rules()];
        await admin.GetServer(admin.GetEndPoints()[0]).ExecuteAsync("ACL", grant);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }
}
