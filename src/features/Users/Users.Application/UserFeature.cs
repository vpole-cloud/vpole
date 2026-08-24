using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.ASP.Helpers;
using Pole.Features.Users.Application.Identity;
using Pole.Features.Users.Application.Persistence;

namespace Pole.Features.Users.Application;

public static class UserFeature
{
    public static IServiceCollection AddUsersFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KeycloakOptions>(configuration.GetSection(KeycloakOptions.SectionName));
        services.AddHttpClient<IIdentityProvider, KeycloakIdentityProvider>();
        services.AddHttpProblemMappings(UserHttpProblemMappings.All);

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
