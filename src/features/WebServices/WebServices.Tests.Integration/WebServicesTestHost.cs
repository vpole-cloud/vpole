// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Testing;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Mediator;
using Pole.Testing;

namespace Pole.Features.WebServices.Tests.Integration;

internal static class WebServicesTestHost
{
    internal static ServiceProvider Create(WebServicesMongoFixture fixture, CreateWebServicePlatform platform)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<ITenantContextAccessor>(
            new TenantContextAccessor
            {
                Current = new TenantContext(TestPlatformDefaults.TenantId, "Test tenant", TenantRole.Member, false),
            });
        services.AddScoped<IMediator, Mediator.Mediator>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantAuthorizationBehavior<,>));
        services.AddScoped<IValidator<CreateWebServiceRequest>, CreateWebServiceRequestValidator>();
        services
            .AddScoped<IMediatorOutcomeHandler<CreateWebServiceRequest, CreateWebServiceOutcome>,
                CreateWebServiceHandler>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(fixture.Database);
        services.AddSingleton(fixture.StoreOutboxWrites);
        services.AddWebServicePersistence();
        services.AddSingleton<IGitOpsMutations, UnmanagedGitOps>();
        services
            .AddScoped<IMediatorOutcomeHandler<UpdateWebServiceRequest, UpdateWebServiceOutcome>,
                UpdateWebServiceHandler>();
        services
            .AddScoped<IMediatorOutcomeHandler<RollbackWebServiceRequest, RollbackWebServiceOutcome>,
                RollbackWebServiceHandler>();
        services
            .AddScoped<IMediatorOutcomeHandler<GetWebServicesRequest, GetWebServicesOutcome>, GetWebServicesHandler>();
        services
            .AddScoped<IMediatorOutcomeHandler<GetWebServiceReleasesRequest, GetWebServiceReleasesOutcome>,
                GetWebServiceReleasesHandler>();
        platform.RegisterContracts(services, fixture.Client);
        ResourceGroupTestData.RegisterCreationContracts(services);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
