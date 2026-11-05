using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Application.Identity;
using Pole.Features.Users.Application.Mongo;
using Pole.Features.Users.Application.Registration;

namespace Pole.Features.Users.Application;

public static class UserFeature
{
    public static IServiceCollection AddUsersFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<PoleUser>("Users"));
        services.Configure<KeycloakOptions>(configuration.GetSection(KeycloakOptions.SectionName));
        services.AddHttpClient<IIdentityProvider, KeycloakIdentityProvider>();

        services.TryAddScoped<MongoEnsureUserEmailAvailable>();
        services.TryAddScoped<EnsureUserEmailAvailable>(provider => provider
            .GetRequiredService<MongoEnsureUserEmailAvailable>()
            .InvokeAsync);
        services.TryAddScoped<MongoPersistCreatedUser>();
        services.TryAddScoped<PersistCreatedUser>(provider => provider
            .GetRequiredService<MongoPersistCreatedUser>()
            .InvokeAsync);

        return services;
    }
}
