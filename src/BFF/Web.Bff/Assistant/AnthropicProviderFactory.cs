using Anthropic;
using Common.Assistant.Anthropic;
using Common.Assistant.Providers;

namespace Web.Bff.Assistant;

/// <summary>The Anthropic provider, built in a type of its own so the SDK loads only with the assistant on.</summary>
internal static class AnthropicProviderFactory
{
    /// <summary>The SDK's own client, reading the <c>ANTHROPIC_API_KEY</c> a secret store sets (ADR-151).</summary>
    public static IAssistantProvider Create(AssistantOptions options, TimeProvider time) =>
        new AnthropicProvider(
            new AnthropicClient(),
            new AnthropicSettings(options.Model, options.Effort).Validated(AssistantOptions.SectionName),
            time);
}
