using Microsoft.Extensions.DependencyInjection;

namespace Common.Infrastructure.Identity;

/// <summary>§11.5's token transport, registered once for every host that mints a client-credentials token.</summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary><see cref="CachingTokenClient"/>'s named client over <paramref name="authority"/>.</summary>
        /// <remarks>
        /// No <see cref="ClientCredentialsHandler"/>, which would recurse, and no redirect followed: a 307 or 308
        /// replays the form, and the form is this host's client secret (§11.5).
        /// </remarks>
        public IHttpClientBuilder AddTokenClient(string authority) =>
            services
                .AddHttpClient(
                    CachingTokenClient.HttpClientName,
                    client => client.BaseAddress = new Uri(authority.TrimEnd('/') + "/"))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
    }
}
