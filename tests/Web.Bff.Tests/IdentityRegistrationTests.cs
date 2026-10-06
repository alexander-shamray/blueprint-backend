using Common.Infrastructure.Identity;
using Common.Web;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The half of §11.5 a host keeps under ADR-052: the names the building block cannot write down.</summary>
public sealed class IdentityRegistrationTests
{
    [Fact]
    public void The_host_carries_the_authority_keys_name_to_the_token_client()
    {
        using BffFactory factory = new();

        // Common.Infrastructure may not name Common.Web, so the key's name travels as a value.
        factory.Services
            .GetRequiredService<AuthorityKeyName>()
            .Name.ShouldBe(AuthenticationExtensions.AuthorityKey);
    }
}
