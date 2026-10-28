namespace WheelWizard.DiscImages;

public static class DiscImagesExtensions
{
    public static IServiceCollection AddDiscImages(this IServiceCollection services)
    {
        services.AddSingleton<IDiscImageService, DiscImageService>();
        services.AddSingleton<IWitRunner, WitRunner>();
        services.AddSingleton<ILineColorService, LineColorService>();
        services.AddSingleton<IWitDownloader, WitDownloader>();
        return services;
    }
}
