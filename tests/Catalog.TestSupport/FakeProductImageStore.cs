using System.Collections.Concurrent;
using Catalog.Application.Products;

namespace Catalog.TestSupport;

/// <summary>The object store, stood in for (ADR-106): what a test uploaded, and what an attach did with it.</summary>
/// <remarks>
/// The S3 adapter is tested against a real store in <c>Platform.IntegrationTests</c>; a handler or endpoint test
/// needs the port's answers and not a server, so it stages the answer here.
/// </remarks>
public sealed class FakeProductImageStore : IProductImageStore
{
    /// <summary>The public base a fake attach builds its URL from; <c>.test</c> never resolves.</summary>
    public const string PublicBase = "https://images.test";

    private readonly ConcurrentDictionary<string, ImageAttachOutcome> _uploads = new();

    /// <summary>The copies made, in order: source key, destination key and the type the handler asked for.</summary>
    public ConcurrentQueue<(string Source, string Destination, string ContentType)> Copies { get; } = new();

    /// <summary>Forgets every upload and copy, since one host serves a whole collection of tests.</summary>
    public void Reset()
    {
        _uploads.Clear();
        Copies.Clear();
    }

    /// <summary>Stages an object at <paramref name="key"/> that the next attach finds, or finds wanting.</summary>
    public void Upload(string key, ImageAttachOutcome outcome = ImageAttachOutcome.Attached) =>
        _uploads[key] = outcome;

    public Task<ImageAttachment> AttachAsync(
        string sourceKey,
        string destinationKey,
        string contentType,
        CancellationToken ct)
    {
        if (!_uploads.TryGetValue(sourceKey, out ImageAttachOutcome outcome))
            return Task.FromResult(new ImageAttachment(ImageAttachOutcome.NotUploaded));

        if (outcome != ImageAttachOutcome.Attached)
            return Task.FromResult(new ImageAttachment(outcome));

        Copies.Enqueue((sourceKey, destinationKey, contentType));
        return Task.FromResult(new ImageAttachment(ImageAttachOutcome.Attached, $"{PublicBase}/{destinationKey}"));
    }
}
