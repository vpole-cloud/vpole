// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Contracts.HttpMapping;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.ComputeProfiles.Application;

public static class ComputeProfileApplicationRegistration
{
    public static IServiceCollection AddComputeProfilesFeature(this IServiceCollection services)
    {
        services.AddHttpProblemMappings(ComputeProfileContractHttpMappings.All);
        services.AddComputeProfilePersistence();
        services.AddSingleton(new RealtimeTopic("compute-profiles", RealtimeScope.Platform));
        services.AddSingleton(new RealtimeTopic("compute-eligibility", RealtimeScope.Tenant));

        services.AddScoped<KubernetesComputeProfileTargetMappingValidator>();
        services.AddScoped<ValidateComputeProfileTargetMappings>(serviceProvider =>
            serviceProvider.GetRequiredService<KubernetesComputeProfileTargetMappingValidator>().InvokeAsync);
        services.AddScoped<MongoComputeProfileTenantAccessValidator>();
        services.AddScoped<ValidateComputeProfileTenantAccess>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoComputeProfileTenantAccessValidator>().InvokeAsync);

        services.TryAddSingleton<ComputeProfileBootstrapper>();
        services.TryAddSingleton<BootstrapComputeProfiles>(serviceProvider =>
            serviceProvider.GetRequiredService<ComputeProfileBootstrapper>().BootstrapAsync);
        return services;
    }
}
