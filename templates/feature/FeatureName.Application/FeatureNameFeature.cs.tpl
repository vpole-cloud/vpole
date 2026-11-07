using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.FeatureName.Application.Creation;
using Pole.Features.FeatureName.Application.Endpoints;
using Pole.Features.FeatureName.Application.Mongo;
using Pole.Features.FeatureName.Application.Reads;
using Pole.Features.FeatureName.Application.Updates;
using Pole.Features.FeatureName.Domain;

namespace Pole.Features.FeatureName.Application;

public static class FeatureNameFeature
{
    public static IServiceCollection AddFeatureNameFeature(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<ResourceName>(FeatureNameCollections.Resources));
        services.TryAddScoped<MongoPersistCreatedResourceName>();
        services.TryAddScoped<PersistCreatedResourceName>(provider =>
            provider.GetRequiredService<MongoPersistCreatedResourceName>().InvokeAsync);
        services.TryAddScoped<RunMongoOutcomeTransaction<PersistCreatedResourceNameOutcome>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        services.TryAddScoped<MongoLoadResourceName>();
        services.TryAddScoped<LoadResourceName>(provider =>
            provider.GetRequiredService<MongoLoadResourceName>().InvokeAsync);
        services.TryAddScoped<MongoPersistUpdatedResourceName>();
        services.TryAddScoped<PersistUpdatedResourceName>(provider =>
            provider.GetRequiredService<MongoPersistUpdatedResourceName>().InvokeAsync);
        services.TryAddScoped<RunMongoOutcomeTransaction<PersistUpdatedResourceNameOutcome>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        return services;
    }

    public static IEndpointRouteBuilder MapFeatureNameEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/feature-route");
        group.MapCreateResourceNameEndpoint();
        group.MapGetResourceNameEndpoint();
        group.MapUpdateResourceNameEndpoint();
        return endpoints;
    }
}
