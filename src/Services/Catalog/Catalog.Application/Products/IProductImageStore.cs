namespace Catalog.Application.Products;

/// <summary>How an attach ended; closed, since each failure is a distinct rule the seller can act on.</summary>
public enum ImageAttachOutcome
{
    /// <summary>The object was copied to its destination, and <see cref="ImageAttachment.Url"/> names it.</summary>
    Attached,

    /// <summary>Nothing sits at the source key: never uploaded, expired, or the upload was refused.</summary>
    NotUploaded,

    /// <summary>The object's first bytes disagree with its stored type, or its type is not an allowed one.</summary>
    NotAnImage
}

/// <summary>The result of <see cref="IProductImageStore.AttachAsync"/>; the URL is set on success alone.</summary>
public sealed record ImageAttachment(ImageAttachOutcome Outcome, string? Url = null);

/// <summary>The port to the object store (ADR-106), the only place a service touches an image's bytes.</summary>
public interface IProductImageStore
{
    /// <summary>
    /// Checks the object at <paramref name="sourceKey"/> is the image <paramref name="contentType"/> says, copies it
    /// to <paramref name="destinationKey"/> with fixed metadata, and returns the public URL built from the key (§10.8).
    /// </summary>
    Task<ImageAttachment> AttachAsync(
        string sourceKey,
        string destinationKey,
        string contentType,
        CancellationToken ct);
}
