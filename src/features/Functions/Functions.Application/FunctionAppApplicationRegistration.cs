using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Secrets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Primitives;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceConnections.Contracts;

namespace Pole.Features.Functions.Application;

public static class FunctionAppApplicationRegistration
{
    public static IServiceCollection AddFunctionAppsFeature(
        this IServiceCollection services)
    {
        services.AddFunctionAppPersistence();
        services.AddScoped<IDeclarativeResourceProvider, DeclarativeFunctionProvider>();
        services.AddSingleton(new SecretDeclarationConsumer(
            "FunctionApp", FunctionResourceDeployment.ResourceType, SecretBindingUsage.Runtime));
        services.AddSingleton(new ResourceConnectionDeclarationKind("FunctionApp",
            FunctionResourceDeployment.ResourceType));
        services.AddSingleton(new ResourceConnectionSourceCapability(
            FunctionResourceDeployment.ResourceType));
        services.TryAddSingleton<FunctionAppCountQuota>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Quota, FunctionAppCountQuota>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IQuotaProfile, FunctionAppQuotaProfile>());
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
