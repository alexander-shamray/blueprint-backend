using Common.Infrastructure.Redis;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

public sealed class RedisKeysTests
{
    private static readonly RedisKeys Keys = new(new TestEnvironment("catalog"));

    [Fact]
    public void Lock_key_carries_the_service_prefix_and_the_lock_namespace() =>
        Keys.Lock("reprice").ShouldBe("catalog:lock:reprice");

    [Fact]
    public void Idempotency_key_uses_the_idem_namespace() =>
        Keys.Idempotency("PlaceOrderCommand:0195e4b2").ShouldBe("catalog:idem:PlaceOrderCommand:0195e4b2");

    [Fact]
    public void Cache_prefix_is_exposed_as_an_instance_name_not_a_key_builder() =>
        Keys.CacheInstanceName.ShouldBe("catalog:cache:");

    [Fact]
    public void The_prefix_is_ApplicationName_verbatim_with_no_normalisation() =>
        new RedisKeys(new TestEnvironment("Catalog.Api")).Lock("x").ShouldBe("Catalog.Api:lock:x");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_suffix_is_rejected(string suffix)
    {
        Should.Throw<ArgumentException>(() => Keys.Lock(suffix));
        Should.Throw<ArgumentException>(() => Keys.Idempotency(suffix));
    }
}

/// <summary>A minimal <see cref="IHostEnvironment"/>, as the helpers read only <c>ApplicationName</c>.</summary>
internal sealed class TestEnvironment(string applicationName) : IHostEnvironment
{
    public string ApplicationName { get; set; } = applicationName;

    public string EnvironmentName { get; set; } = Environments.Development;

    public string ContentRootPath { get; set; } = string.Empty;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
