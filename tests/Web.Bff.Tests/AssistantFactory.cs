using Common.Assistant.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Web.Bff.Assistant;

namespace Web.Bff.Tests;

/// <summary>The real BFF with ADR-151's assistant on and <see cref="ScriptedProvider"/> as its provider.</summary>
public sealed class AssistantFactory(params KeyValuePair<string, string?>[] settings) : BffFactory
{
    public ScriptedProvider Provider { get; } = new();

    protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        .. base.Settings,
        new($"{AssistantOptions.SectionName}:{nameof(AssistantOptions.Provider)}", AssistantOptions.Anthropic),
        .. settings
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // After the host's factory registration, so the runtime resolves this and the SDK is never built.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAssistantProvider>();
            services.AddSingleton<IAssistantProvider>(Provider);
        });
    }
}
