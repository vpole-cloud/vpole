using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application;

public static class StorageProfileApplicationRegistration
{
    public static IServiceCollection AddStorageProfileResolution(
        this IServiceCollection services)
    {
        services.AddHttpProblemMappings(StorageProfileContractHttpMappings.All);
        services.TryAddScoped<StorageProfileResolver>();
        services.TryAddScoped<ResolveStorageProfile>(serviceProvider =>
            serviceProvider.GetRequiredService<StorageProfileResolver>().InvokeAsync);
        services.TryAddScoped<StorageProfileLegacyResolverAdapter>();
        services.TryAddScoped<IStorageProfileResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<StorageProfileLegacyResolverAdapter>());
        return services;
    }
}
