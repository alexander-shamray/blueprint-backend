using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Notifications.Api.Tests;

/// <summary>ADR-120's required pairs, the one jurisdiction member this host reads, and no other.</summary>
public sealed class RequiredPairsTests
{
    private sealed class RequiringFactory(params string[] required) : NotificationsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            for (int i = 0; i < required.Length; i++)
                builder.UseSetting($"Jurisdiction:Required:{i}", required[i]);
        }
    }

    [Fact]
    public void The_host_starts_on_a_stated_pair_without_the_workers_windows_or_language_set()
    {
        // The control for the refusal below, and the reason the API validates only what it reads.
        using RequiringFactory factory = new("order-placed/email");

        Should.NotThrow(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("order-placed/sms")]
    [InlineData("newsletter/email")]
    public void The_host_refuses_to_start_on_a_pair_outside_the_vocabulary(string pair)
    {
        using RequiringFactory factory = new(pair);

        Should.Throw<OptionsValidationException>(() => factory.CreateClient()).Message.ShouldContain(pair);
    }
}
