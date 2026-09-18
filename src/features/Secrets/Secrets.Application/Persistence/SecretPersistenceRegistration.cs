using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
namespace Pole.Features.Secrets.Application;

internal static class SecretPersistenceRegistration
{
    internal static IServiceCollection AddSecretPersistence(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<Secret>("Secrets"));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<SecretVersion>("SecretVersions"));
        services.AddScoped<SecretStore>();
        services.AddScoped<IIsolationScopeReferenceGuard, SecretScopeReferenceGuard>();
        return services;
    }
}
