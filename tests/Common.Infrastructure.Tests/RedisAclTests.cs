using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§8.1's grant keeps a service out of another's keys, their names included.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class RedisAclTests(RedisFixture fixture)
{
    [Theory]
    [InlineData("SCAN", new object[] { "0" })]
    [InlineData("RANDOMKEY", new object[0])]
    [InlineData("DBSIZE", new object[0])]
    public async Task A_command_that_takes_no_key_is_refused(string command, object[] arguments)
    {
        await using ConnectionMultiplexer connection = await ConnectAsAsync("aclkeys-svc", "aclkeys:*");
        IDatabase database = connection.GetDatabase();

        // Its own key works, so the refusal below is the grant's and not a dead connection.
        await database.StringSetAsync("aclkeys:own", "1");
        ((string?)await database.StringGetAsync("aclkeys:own")).ShouldBe("1");

        // A key pattern binds only a command that names a key, so these would list or count every service's keys.
        RedisServerException refused = await Should.ThrowAsync<RedisServerException>(
            () => database.ExecuteAsync(command, arguments));
        refused.Message.ShouldStartWith("NOPERM");
    }

    [Fact]
    public async Task Another_service_s_key_is_refused()
    {
        await using ConnectionMultiplexer connection = await ConnectAsAsync("aclpeer-svc", "aclpeer:*");

        RedisServerException refused = await Should.ThrowAsync<RedisServerException>(
            () => connection.GetDatabase().StringGetAsync("Ordering.Api:cache:x"));
        refused.Message.ShouldStartWith("NOPERM");
    }

    [Fact]
    public void Every_service_user_in_users_conf_carries_the_rules_these_cases_prove()
    {
        // The rules are read from Catalog's line; a second service granted otherwise would be proved by nothing.
        string[] users = [.. DocumentedRedisGrant.UserLines()];
        users.Length.ShouldBeGreaterThan(1, "a file read as one user or none proves nothing about the rest");
        foreach (string line in users)
            DocumentedRedisGrant.RulesOf(line).ShouldBe(DocumentedRedisGrant.Rules(), line);
    }

    private async Task<ConnectionMultiplexer> ConnectAsAsync(string user, string keys)
    {
        ConfigurationOptions admin = ConfigurationOptions.Parse(fixture.CoordinationConnectionString);
        admin.AllowAdmin = true;
        await using ConnectionMultiplexer adminConnection = await ConnectionMultiplexer.ConnectAsync(admin);
        await DocumentedRedisGrant.ProvisionAsync(adminConnection, user, "s3cret", keys);

        ConfigurationOptions restricted = ConfigurationOptions.Parse(fixture.CoordinationConnectionString);
        restricted.User = user;
        restricted.Password = "s3cret";
        return await ConnectionMultiplexer.ConnectAsync(restricted);
    }
}
