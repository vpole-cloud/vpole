using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Database;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Contracts;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application;

public static class WebServicesFeatureRegistration
{
    public static IServiceCollection AddWebServicesFeature(
        this IServiceCollection services)
    {
        services.AddWebServicePersistence();
        services.AddOutcomeTransaction<WebServiceDeploymentHostnameOutcome>();
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
            Result<SourceBuildDeploymentResult, SourceBuildWebServiceDeploymentError>>();
        services.TryAddScoped<WebServiceResourceDeploymentAdapter>();
        services.AddSingleton(WebServiceResourceDeploymentAdapterRegistration.Descriptor);
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
