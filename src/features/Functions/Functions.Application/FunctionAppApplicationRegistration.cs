using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.ASP.Helpers;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Application.Persistence.Lifecycle;

namespace Pole.Features.Functions.Application;

public static class FunctionAppApplicationRegistration
{
    public static IServiceCollection AddFunctionAppsFeature(
        this IServiceCollection services)
    {
        services.AddHttpProblemMappings(FunctionAppHttpMappings.All);
        services.AddFunctionAppPersistence();
        services.TryAddScoped<MongoFunctionRevisionLifecycleStore>();
        services.TryAddScoped<MongoMarkFunctionRevisionReady>();
        services.TryAddScoped<MarkFunctionRevisionReady>(serviceProvider => serviceProvider
            .GetRequiredService<MongoMarkFunctionRevisionReady>()
            .InvokeAsync);
        services.TryAddScoped<MongoPromoteFunctionRevision>();
        services.TryAddScoped<PromoteFunctionRevision>(serviceProvider => serviceProvider
            .GetRequiredService<MongoPromoteFunctionRevision>()
            .InvokeAsync);
        services.TryAddScoped<MongoFailFunctionRevision>();
        services.TryAddScoped<FailFunctionRevision>(serviceProvider => serviceProvider
            .GetRequiredService<MongoFailFunctionRevision>()
            .InvokeAsync);
        services.TryAddScoped<MongoRetireFunctionRevision>();
        services.TryAddScoped<RetireFunctionRevision>(serviceProvider => serviceProvider
            .GetRequiredService<MongoRetireFunctionRevision>()
            .InvokeAsync);
        return services;
    }
}
