using Catalog.Application.Products;
using Catalog.Application.Products.PublishProduct;
using Catalog.TestSupport;
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>§10.8's attach: what publishing does with the key an upload returned, through the real container.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PublishProductImageTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Seller = Guid.CreateVersion7();

    private FakeProductImageStore Store => fixture.Factory.ImageStore;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        Store.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Result<Guid>> PublishAsync(string? imageKey, Guid? caller = null)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await Caller.SendAsync(
            scope.ServiceProvider,
            new PublishProductCommand(Guid.CreateVersion7(), "Walnut desk", imageKey, 19.99m, "EUR"),
            caller ?? Seller);
    }

    private Task<string?> ThumbnailAsync(Guid product) =>
        fixture.ScalarAsync<string?>("SELECT Value = ThumbnailUrl FROM catalog.Products WHERE Id = {0}", product);

    private Task<int> ProductCountAsync() =>
        fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");

    [Fact]
    public async Task The_stored_url_is_built_from_the_destination_key_and_the_configured_base()
    {
        Guid image = Guid.CreateVersion7();
        string upload = ProductImageKeys.UploadKey(Seller, image, "png");
        Store.Upload(upload);

        Result<Guid> result = await PublishAsync(upload);

        result.IsSuccess.ShouldBeTrue();

        // Rebuilt from the key, never echoed (§10.8), and under the destination prefix with the same GUID.
        string destination = ProductImageKeys.ProductKey(Seller, image, "png");
        (await ThumbnailAsync(result.Value)).ShouldBe($"{FakeProductImageStore.PublicBase}/{destination}");

        // The type is the key's extension's and not the caller's.
        Store.Copies.ShouldContain((upload, destination, "image/png"));
    }

    [Fact]
    public async Task A_product_published_without_a_key_has_no_image_and_copies_nothing()
    {
        Result<Guid> result = await PublishAsync(null);

        result.IsSuccess.ShouldBeTrue();
        (await ThumbnailAsync(result.Value)).ShouldBeNull();
        Store.Copies.ShouldBeEmpty();
    }

    public static TheoryData<string> KeysThatAreNotThisSellersUpload() => new()
    {
        // Another seller's upload, which the prefix check is what stops attaching.
        ProductImageKeys.UploadKey(Guid.CreateVersion7(), Guid.CreateVersion7(), "jpg"),
        // A URL of any scheme in the key's place is not a key.
        "https://evil.example/desk.jpg",
        "javascript:alert(1)",
        // A published key, which is a place to copy to and not a place to copy from.
        "products/00000000-0000-0000-0000-000000000000/00000000-0000-0000-0000-000000000000.jpg",
        "uploads/not-a-seller/not-an-image.jpg",
        // An extension outside the allow-list, SVG above all.
        "uploads/00000000-0000-0000-0000-000000000000/00000000-0000-0000-0000-000000000000.svg"
    };

    [Theory]
    [MemberData(nameof(KeysThatAreNotThisSellersUpload))]
    public async Task A_key_that_is_not_this_sellers_upload_is_refused_and_publishes_nothing(string key)
    {
        // Staged under every spelling, so a refusal is the key's shape and not an absent object.
        Store.Upload(key);

        Result<Guid> result = await PublishAsync(key);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ProductErrors.ImageKeyInvalid);
        Store.Copies.ShouldBeEmpty();
        (await ProductCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_key_with_nothing_behind_it_is_refused_and_publishes_nothing()
    {
        Result<Guid> result = await PublishAsync(ProductImageKeys.UploadKey(Seller, Guid.CreateVersion7(), "jpg"));

        result.Error.ShouldBe(ProductErrors.ImageNotUploaded);
        (await ProductCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task An_object_that_is_not_the_image_it_declares_is_refused_and_publishes_nothing()
    {
        string upload = ProductImageKeys.UploadKey(Seller, Guid.CreateVersion7(), "webp");
        Store.Upload(upload, ImageAttachOutcome.NotAnImage);

        Result<Guid> result = await PublishAsync(upload);

        result.Error.ShouldBe(ProductErrors.ImageNotAnImage);
        Store.Copies.ShouldBeEmpty();
        (await ProductCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_dispatch_with_no_principal_cannot_attach_an_image()
    {
        string upload = ProductImageKeys.UploadKey(Seller, Guid.CreateVersion7(), "jpg");
        Store.Upload(upload);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Result<Guid> result = await scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(
                new PublishProductCommand(Guid.CreateVersion7(), "Walnut desk", upload, 19.99m, "EUR"),
                TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ProductErrors.ImageKeyInvalid);
    }
}
