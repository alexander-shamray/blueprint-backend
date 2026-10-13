using Amazon.Runtime;
using Amazon.S3;
using Catalog.Application.Products;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Catalog.Infrastructure.ProductImages;

/// <summary>ADR-106's store: its options, checked at start, and the client over them.</summary>
internal static class ProductImageRegistration
{
    public static IServiceCollection AddProductImageStore(this IServiceCollection services)
    {
        // Validated at start, as every host's options are (§15.4): IOptions<T> always resolves, so a forgotten
        // binding would otherwise show as the first publish's 500.
        services
            .AddOptions<ProductImageStoreOptions>()
            .BindConfiguration(ProductImageStoreOptions.SectionName)
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ProductImageStoreOptions>, ProductImageStoreOptionsValidator>();

        services.AddSingleton<IAmazonS3>(sp =>
        {
            ProductImageStoreOptions store = sp.GetRequiredService<IOptions<ProductImageStoreOptions>>().Value;

            return new AmazonS3Client(
                new BasicAWSCredentials(store.AccessKey, store.SecretKey),
                new AmazonS3Config
                {
                    ServiceURL = store.Endpoint!.ToString(),
                    AuthenticationRegion = store.Region,
                    ForcePathStyle = store.PathStyle,

                    // Not every S3-compatible store accepts the default's trailing checksums; the signature covers
                    // the request without them.
                    RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                    ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
                });
        });

        services.AddSingleton<IProductImageStore, S3ProductImageStore>();

        return services;
    }
}
