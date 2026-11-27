// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.Users.Application.Identity;
using Pole.Features.Users.Application.Registration;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Contracts;

namespace Pole.Features.Users.Application;

public static class UserFeature
{
    public static IServiceCollection AddUsersFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<PoleUser>("Users"));
        services.Configure<KeycloakOptions>(configuration.GetSection(KeycloakOptions.SectionName));
        services.AddHttpClient<IIdentityProvider, KeycloakIdentityProvider>();
        services.AddHttpClient<KeycloakIdentityProvider>();
        services.TryAddScoped<StartInvitedIdentity>(provider => provider
            .GetRequiredService<KeycloakIdentityProvider>()
            .StartInvitationAsync);

        services.TryAddScoped<MongoReconcileUserDefaultTenant>();
        services.TryAddScoped<ReconcileUserDefaultTenant>(provider => provider
            .GetRequiredService<MongoReconcileUserDefaultTenant>()
            .InvokeAsync);
        services.TryAddScoped<MongoJoinInvitedUser>();
        services.TryAddScoped<JoinInvitedUser>(provider => provider
            .GetRequiredService<MongoJoinInvitedUser>()
            .InvokeAsync);

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
