using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Application;

/// <summary>The SHA-256 of a command as the pipeline sees it, which §8.5 stores beside its result.</summary>
/// <remarks>Defaults are omitted: a new optional field leaves an old request's fingerprint alone (ADR-057).</remarks>
internal static class CommandFingerprint
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public static string Of<TCommand>(TCommand command) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, Options)));
}
