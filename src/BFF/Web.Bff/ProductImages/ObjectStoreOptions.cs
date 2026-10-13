using System.ComponentModel.DataAnnotations;

namespace Web.Bff.ProductImages;

/// <summary>The store this host signs uploads for (ADR-106), bound from <c>ProductImages:Store</c>.</summary>
/// <remarks>
/// The credential pair can write under <c>uploads/</c> and nothing else (§10.8); a deployment supplies both halves
/// from its secret store (§15.4), and Compose's local pair is the exception <c>docs/secrets.md</c> lists.
/// </remarks>
public sealed class ObjectStoreOptions
{
    public const string SectionName = "ProductImages:Store";

    /// <summary>The S3 endpoint the client will <c>PUT</c> to, as the client reaches it.</summary>
    [Required] public Uri? Endpoint { get; init; }

    [Required] public string Bucket { get; init; } = "";

    [Required] public string Region { get; init; } = "";

    [Required] public string AccessKey { get; init; } = "";

    [Required] public string SecretKey { get; init; } = "";

    /// <summary>True for <c>endpoint/bucket/key</c>; false for <c>bucket.endpoint/key</c>.</summary>
    public bool PathStyle { get; init; }
}
