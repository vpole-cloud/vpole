using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

public static class WebServicePersistenceRegistration
{
    public static IServiceCollection AddWebServicePersistence(this IServiceCollection services)
    {
        services.AddWebServiceHttpMappings();
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<WebService>("WebServices"));
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<WebServiceRelease>("WebServiceReleases"));
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<WebServiceDeploymentToken>("WebServiceDeploymentTokens"));
        services.AddCapability<MongoLoadWebServiceById, LoadWebServiceById>(x => x.InvokeAsync);
        services.AddCapability<MongoWebServiceExists, WebServiceExists>(x => x.InvokeAsync);
        services.AddCapability<MongoLoadWebServicePage, LoadWebServicePage>(x => x.InvokeAsync);
        services.AddCapability<MongoLoadWebServiceReleaseHistory, LoadWebServiceReleaseHistory>(x => x.InvokeAsync);
        services.AddCapability<MongoLoadSucceededWebServiceRelease, LoadSucceededWebServiceRelease>(x => x.InvokeAsync);
        services.AddCapability<MongoLoadWebServiceDeploymentSnapshot, LoadWebServiceDeploymentSnapshot>(x => x.InvokeAsync);
        services.AddCapability<MongoPersistCreatedWebService, PersistCreatedWebService>(x => x.InvokeAsync);
        services.AddCapability<MongoPersistUpdatedWebService, PersistUpdatedWebService>(x => x.InvokeAsync);
        services.AddCapability<MongoPersistUpdatedWebService, PersistUpdatedWebServiceOutcome>(x => x.InvokeOutcomeAsync);
        services.AddCapability<MongoPersistRedeployedWebService, PersistRedeployedWebService>(x => x.InvokeAsync);
        services.AddCapability<MongoPersistCurrentWebServiceRelease, PersistCurrentWebServiceRelease>(x => x.InvokeAsync);
        services.AddCapability<MongoBeginWebServiceDeletion, BeginWebServiceDeletion>(x => x.InvokeAsync);
        services.AddCapability<MongoFinalizeWebServiceDeletion, FinalizeWebServiceDeletion>(x => x.InvokeAsync);
        services.AddCapability<MongoCompleteWebServiceDeployment, CompleteWebServiceDeployment>(x => x.InvokeAsync);
        services.AddCapability<MongoLoadWebServiceDeploymentTokenByHash, LoadWebServiceDeploymentTokenByHash>(x => x.InvokeAsync);
        services.AddCapability<MongoPersistIssuedWebServiceDeploymentToken, PersistIssuedWebServiceDeploymentToken>(x => x.InvokeAsync);
        services.AddCapability<MongoLoadWebServiceDeploymentTokens, LoadWebServiceDeploymentTokens>(x => x.InvokeAsync);
        services.AddCapability<MongoRevokeWebServiceDeploymentToken, RevokeWebServiceDeploymentToken>(x => x.InvokeAsync);
        services.AddCapability<MongoTouchWebServiceDeploymentToken, TouchWebServiceDeploymentToken>(x => x.InvokeAsync);
        services.AddResultTransaction<RedeployWebServiceResponse>();
        services.AddResultTransaction<UpdateWebServiceImageTagResponse>();
        services.AddResultTransaction<SourceBuildDeploymentResult>();
        services.AddResultTransaction<Pole.Features.ResourceDeployments.Contracts.PlacementSummaryDto>();
        return services;
    }

    private static void AddCapability<TImplementation, TDelegate>(
        this IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.AddScoped<TImplementation>();
        services.AddScoped(serviceProvider =>
            bind(serviceProvider.GetRequiredService<TImplementation>()));
    }

    private static void AddResultTransaction<T>(this IServiceCollection services)
        where T : notnull
    {
        // The built-in container cannot bind an open generic delegate factory,
        // so each consumed closed result type is registered explicitly above.
        services.TryAddSingleton<RunMongoTransaction<T>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeAsync<T>);
    }
}
