// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.HostnameBindings;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Contracts.HttpMapping;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Domains.Application;

public static class DomainsFeatureRegistration
{
    public static IServiceCollection AddDomainsFeature(this IServiceCollection services)
    {
        services.AddSingleton(new RealtimeTopic("domains", RealtimeScope.Tenant));
        services.AddDomainPersistence();
        services.AddScoped<DeclarativeDomainZoneProvider>();
        services.AddScoped<DeclarativeHostnameBindingProvider>();
        services.AddScoped<IDeclarativeResourceProvider>(provider =>
            provider.GetRequiredService<DeclarativeDomainZoneProvider>());
        services.AddScoped<IDeclarativeResourceProvider>(provider =>
            provider.GetRequiredService<DeclarativeHostnameBindingProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(provider =>
            provider.GetRequiredService<DeclarativeDomainZoneProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(provider =>
            provider.GetRequiredService<DeclarativeHostnameBindingProvider>());
        services.AddScoped<IDeclarativeLifecycleDependencyProvider, DomainLifecycleDependencies>();
        services.AddHttpProblemMappings(DomainContractHttpMappings.All);
        services.AddHttpProblemMappings(DomainHttpMappings.All);
        return services;
    }
}
