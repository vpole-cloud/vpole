using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
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
        services.AddCapability<MongoPersistUpdatedWebService, PersistUpdatedWebServiceOutcome>(x => x.InvokeOutcomeAsync);
        services.AddCapability<MongoPersistCurrentWebServiceRelease, PersistCurrentWebServiceRelease>(x => x.InvokeAsync);
        services.AddCapability<MongoBeginWebServiceDeletion, BeginWebServiceDeletionOutcome>(x => x.InvokeOutcomeAsync);
        services.AddCapability<MongoFinalizeWebServiceDeletion, FinalizeWebServiceDeletion>(x => x.InvokeAsync);
        services.AddCapability<MongoCompleteWebServiceDeployment, CompleteWebServiceDeployment>(x => x.InvokeAsync);
        services.AddCapability<MongoLoadWebServiceDeploymentTokenByHash, LoadWebServiceDeploymentTokenByHash>(x => x.InvokeAsync);
        services.AddCapability<MongoPersistIssuedWebServiceDeploymentToken, PersistIssuedWebServiceDeploymentToken>(x => x.InvokeAsync);
        services.AddCapability<MongoLoadWebServiceDeploymentTokens, LoadWebServiceDeploymentTokens>(x => x.InvokeAsync);
        services.AddCapability<MongoRevokeWebServiceDeploymentToken, RevokeWebServiceDeploymentTokenOutcome>(x => x.InvokeOutcomeAsync);
        services.AddCapability<MongoTouchWebServiceDeploymentToken, TouchWebServiceDeploymentToken>(x => x.InvokeAsync);
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
}
