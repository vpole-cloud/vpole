using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Database;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Deployments;
using Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Deployments;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Application.Search;
using Pole.Features.WebServices.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.Search.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application;

public static class WebServicesFeatureRegistration
{
    public static IServiceCollection AddWebServicesFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<SearchSourceType>(WebServiceSearchProjection.SourceType);
        services.AddWebServicePersistence();
        services.AddSingleton(new ResourceConnectionSourceCapability(
            WebServiceResourceDeployment.ResourceType));
        services.TryAddSingleton<WebServiceCountQuota>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Quota, WebServiceCountQuota>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IQuotaProfile, WebServiceQuotaProfile>());
        services.AddOutcomeTransaction<AddWebServiceDeploymentHostnameOutcome>();
        services.AddOutcomeTransaction<DeleteWebServiceDeploymentHostnameOutcome>();
        services.AddOutcomeTransaction<RedeployWebServiceOutcome>();
        services.AddOutcomeTransaction<UpdateWebServiceImageTagOutcome>();
        services.AddOutcomeTransaction<RetryWebServiceDeploymentOutcome>();
        services.AddOutcomeTransaction<FinalizeWebServiceDeletionOutcome>();
        services.AddOutcomeTransaction<CompleteWebServiceDeploymentOutcome>();
        services.TryAddScoped<WebServiceSourceBuildDeploymentService>();
        services.TryAddScoped<DeploySourceBuildWebService>(serviceProvider =>
            serviceProvider
                .GetRequiredService<WebServiceSourceBuildDeploymentService>()
                .DeployAsync);
        services.AddOutcomeTransaction<
            DeploySourceBuildWebServiceOutcome>();
        services.TryAddScoped<WebServiceResourceDeploymentAdapter>();
        services.AddSingleton(WebServiceResourceDeploymentAdapterRegistration.Descriptor);
        services.AddSingleton<ResourceIngressPolicyDefinition>(WebServiceIngressPolicyDefinitions.Public);
        services.AddSingleton<ResourceIngressPolicyDefinition>(WebServiceIngressPolicyDefinitions.Http01);
        return services;
    }

    private static void AddOutcomeTransaction<TOutcome>(
        this IServiceCollection services)
        where TOutcome : notnull
    {
        services.TryAddScoped<RunMongoOutcomeTransaction<TOutcome>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<TOutcome>);
    }
}
