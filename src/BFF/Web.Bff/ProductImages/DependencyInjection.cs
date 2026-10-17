using FluentValidation;

namespace Web.Bff.ProductImages;

/// <summary>E.6.11's upload: its store options, its signer and its validator.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddProductImageUploads(this IServiceCollection services)
    {
        // Validated at start, as every host's options are (§15.4): IOptions<T> always resolves, so a forgotten
        // binding would otherwise show as the first seller's 500.
        services
            .AddOptions<ObjectStoreOptions>()
            .BindConfiguration(ObjectStoreOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ProductImageUploads>();
        services.AddSingleton<IValidator<ProductImageUploadRequest>, ProductImageUploadRequestValidator>();

        return services;
    }
}
