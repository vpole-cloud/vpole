using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Pole.Encryption;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEnvelopeEncryption(
        this IServiceCollection services,
        IConfiguration configuration,
        bool includeLocalKeyFileProvider = true)
    {
        if (includeLocalKeyFileProvider)
        {
            services.AddSingleton<IValidateOptions<LocalKeyFileOptions>, LocalKeyFileOptionsValidator>();
            services.AddOptions<LocalKeyFileOptions>()
                .Bind(configuration.GetSection(LocalKeyFileOptions.SectionName))
                .ValidateOnStart();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IKeyEncryptionProvider, LocalKeyFileKeyEncryptionProvider>());
        }

        services.AddSingleton<IValidateOptions<EnvelopeEncryptionOptions>, EnvelopeEncryptionOptionsValidator>();
        services.AddOptions<EnvelopeEncryptionOptions>()
            .Bind(configuration.GetSection(EnvelopeEncryptionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IEnvelopeEncryptionService, EnvelopeEncryptionService>();
        return services;
    }
}
