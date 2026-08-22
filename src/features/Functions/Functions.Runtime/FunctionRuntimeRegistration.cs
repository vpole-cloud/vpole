using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Metering;
using Pole.Features.Functions.Runtime.Provisioning;
using Pole.Features.Functions.Runtime.Scaling;
using Pole.Features.Functions.Runtime.Security;
using Pole.Features.Functions.Runtime.Timers;

namespace Pole.Features.Functions.Runtime;

public static class FunctionRuntimeRegistration
{
    public static IServiceCollection AddFunctionRuntimeFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        ConfigureAndCache<FunctionGatewayOptions, FunctionGatewayOptionsValidator>(
            services,
            configuration,
            FunctionGatewayOptions.SectionName);
        ConfigureAndCache<FunctionCoordinationOptions, FunctionCoordinationOptionsValidator>(
            services,
            configuration,
            FunctionCoordinationOptions.SectionName);
        ConfigureAndCache<FunctionScalerOptions, FunctionScalerOptionsValidator>(
            services,
            configuration,
            FunctionScalerOptions.SectionName);
        ConfigureAndCache<FunctionTimerOptions, FunctionTimerOptionsValidator>(
            services,
            configuration,
            FunctionTimerOptions.SectionName);

        var functionAgentTokenSection = configuration.GetSection(FunctionAgentTokenOptions.SectionName);
        if (functionAgentTokenSection.Exists())
            services.AddConfiguredFunctionAgentRuntime(functionAgentTokenSection);

        services.TryAddSingleton<IFunctionRevisionKubernetesManager, FunctionRevisionKubernetesManager>();
        services.TryAddSingleton<IFunctionEndpointSliceSource, KubernetesFunctionEndpointSliceSource>();
        services.TryAddSingleton<IFunctionEndpointRegistry, FunctionEndpointRegistry>();
        services.TryAddSingleton<IFunctionRevisionActivator, FunctionRevisionActivator>();
        services.TryAddSingleton<IFunctionRuntimeRouteResolver, FunctionRuntimeRouteResolver>();
        services.AddHostedService<FunctionEndpointSliceWatcher>();
        return services;
    }

    private static void ConfigureAndCache<TOptions, TValidator>(
        IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<TOptions>>().Value);
    }

    private static void AddConfiguredFunctionAgentRuntime(
        this IServiceCollection services,
        IConfigurationSection functionAgentTokenSection)
    {
        var tokenOptions = functionAgentTokenSection.Get<FunctionAgentTokenOptions>() ??
                           throw new InvalidOperationException("Function Agent token configuration is invalid.");
        tokenOptions.Validate();
        services.AddSingleton(tokenOptions);
        services.AddSingleton<IFunctionAgentSigningKeyProvider, FileFunctionAgentSigningKeyProvider>();
        services.AddSingleton<FunctionAgentTokenIssuer>();
        services.AddSingleton<IFunctionAgentTokenIssuer>(provider =>
            provider.GetRequiredService<FunctionAgentTokenIssuer>());
        services.AddSingleton<IFunctionAgentTrustBundleProvider>(provider =>
            provider.GetRequiredService<FunctionAgentTokenIssuer>());
        services.AddHttpClient<IFunctionAgentClient, FunctionAgentClient>(client =>
                client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            });
        services.AddScoped<IFunctionRevisionProvisioner, FunctionRevisionProvisioner>();
        services.TryAddSingleton<IFunctionEndpointSelector, FunctionEndpointSelector>();
        services.TryAddSingleton<FunctionCoordinationClient>();
        services.TryAddSingleton<IFunctionAdmissionCoordinator>(provider =>
            provider.GetRequiredService<FunctionCoordinationClient>());
        services.TryAddSingleton<IFunctionTrafficCoordinator>(provider =>
            provider.GetRequiredService<FunctionCoordinationClient>());
        services.TryAddSingleton<IFunctionActivationCoordinator>(provider =>
            provider.GetRequiredService<FunctionCoordinationClient>());
        services.TryAddSingleton<IFunctionScalingCoordinator>(provider =>
            provider.GetRequiredService<FunctionCoordinationClient>());
        services.TryAddSingleton<IFunctionPendingAdmissionLimiter, FunctionPendingAdmissionLimiter>();
        services.TryAddSingleton<IFunctionPublicRateLimiter, FunctionPublicRateLimiter>();
        services.TryAddSingleton<IFunctionMeterWriter, FunctionMeterWriter>();
        services.TryAddSingleton<IFunctionHttpGateway, FunctionHttpGateway>();
        services.TryAddSingleton<IFunctionScaleTargetSource, MongoFunctionScaleTargetSource>();
        services.TryAddSingleton<IFunctionScaler, FunctionScaler>();
        services.AddHostedService<FunctionScalerWorker>();
        services.TryAddSingleton<IFunctionTimerScheduler, FunctionTimerScheduler>();
        services.AddHostedService<FunctionTimerSchedulerWorker>();
        services.TryAddSingleton<IFunctionInvocationDispatcher, FunctionInvocationDispatcher>();
        services.AddHostedService<FunctionInvocationDispatcherWorker>();
        services.TryAddSingleton<IFunctionUsageAggregator, FunctionUsageAggregator>();
        services.AddHostedService<FunctionUsageAggregatorWorker>();
    }
}
