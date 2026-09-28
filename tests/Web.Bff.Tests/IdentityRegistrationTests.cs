using Common.Infrastructure.Identity;
using Common.Web;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// The half of §11.5 a host keeps after ADR-052: the grant's code is a
/// building block's, and the names it cannot write down are this host's to
/// supply.
/// </summary>
public sealed class IdentityRegistrationTests
{
    [Fact]
    public void The_host_carries_the_authority_keys_name_to_the_token_client()
    {
        using BffFactory factory = new();

        // Common.Infrastructure may not name Common.Web, so the key's name
        // travels as a value and a refused discovery document says which key
        // to fix. Supplied wrongly, every refusal this host's token client
        // writes names a key nobody can set, and no other test would notice.
        factory.Services.GetRequiredService<AuthorityKeyName>()
            .Name.ShouldBe(AuthenticationExtensions.AuthorityKey);
    }
}
