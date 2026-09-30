using System.Net;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>Claims only a host keeping the production authentication scheme can make.</summary>
public class EndpointSecurityTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_the_read_path()
    {
        // This host registers only the production JWT scheme, so TestAuthHandler's headers must be just bytes. No
        // Authorization header, so nothing is fetched from the authority.
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Get, $"/v1/payments/{Guid.CreateVersion7()}");
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, PaymentsPermissions.Admin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_liveness_probe_still_answers_without_a_token()
    {
        // Keeps the test above from passing on a host that refuses everything.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
