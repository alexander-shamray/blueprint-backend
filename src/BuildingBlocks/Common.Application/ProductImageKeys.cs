namespace Common.Application;

/// <summary>The object keys, content types and size of a product image, which two hosts share (§10.8).</summary>
/// <remarks>One owner (§10.8), so the host that mints a key and the host that accepts one cannot differ.</remarks>
public static class ProductImageKeys
{
    /// <summary>Five MiB, §10.8's ceiling on the body the signature allows.</summary>
    public const long MaxSizeBytes = 5 * 1024 * 1024;

    /// <summary>Where an upload waits to be attached, and the prefix the lifecycle rule expires (§10.8).</summary>
    public const string UploadPrefix = "uploads/";

    /// <summary>Where a published image lives, and the only prefix the public host serves (§10.8).</summary>
    public const string ProductPrefix = "products/";

    /// <summary>The longest destination key: <c>products/</c>, two GUIDs, a slash, a dot and <c>webp</c>.</summary>
    public const int LongestDestinationKeyLength = 87;

    // A closed list in which SVG is deliberately absent (§10.8): an SVG is a document that can carry script.
    private static readonly Dictionary<string, string> ExtensionsByType = new(StringComparer.Ordinal)
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp"
    };

    /// <summary>The content types an upload may declare.</summary>
    public static IReadOnlyCollection<string> AllowedContentTypes => ExtensionsByType.Keys;

    /// <summary>The extension for an allowed content type, or null for any other string, case included.</summary>
    public static string? ExtensionFor(string? contentType) =>
        contentType is not null && ExtensionsByType.TryGetValue(contentType, out string? extension) ? extension : null;

    /// <summary>The content type an extension stands for; the inverse of <see cref="ExtensionFor"/>.</summary>
    public static string? ContentTypeFor(string extension) =>
        ExtensionsByType.FirstOrDefault(pair => pair.Value == extension).Key;

    /// <summary>The key the BFF mints for one upload; the client never supplies a segment of it (§10.8).</summary>
    public static string UploadKey(Guid seller, Guid image, string extension) =>
        $"{UploadPrefix}{seller:D}/{image:D}.{extension}";

    /// <summary>The key a published image is copied to, derived from the upload's (§10.8).</summary>
    public static string ProductKey(Guid seller, Guid image, string extension) =>
        $"{ProductPrefix}{seller:D}/{image:D}.{extension}";

    /// <summary>
    /// Reads a key back into its parts, and accepts only the exact shape <see cref="UploadKey"/> writes under this
    /// seller's prefix; another seller's key, a longer path and a differently cased GUID are all refused.
    /// </summary>
    public static bool TryParseUploadKey(string? key, Guid seller, out Guid image, out string extension)
    {
        image = Guid.Empty;
        extension = string.Empty;

        if (key is null)
            return false;

        string prefix = $"{UploadPrefix}{seller:D}/";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string name = key[prefix.Length..];
        int dot = name.LastIndexOf('.');
        if (dot < 0)
            return false;

        string stem = name[..dot];
        string candidate = name[(dot + 1)..];

        // Round-tripped, so the lower-case D form is the only spelling and no GUID parser leniency gets through.
        if (!Guid.TryParseExact(stem, "D", out Guid parsed) || parsed.ToString("D") != stem)
            return false;

        if (ContentTypeFor(candidate) is null)
            return false;

        image = parsed;
        extension = candidate;
        return true;
    }
}
