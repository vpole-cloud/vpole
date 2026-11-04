using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Secrets.Application.Endpoints;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Secrets.Application;

public static class SecretFeature
{
    public static IServiceCollection AddSecretsFeature(this IServiceCollection services)
    {
        services.AddSingleton(new RealtimeTopic("secrets", RealtimeScope.Tenant));
        services.AddSecretPersistence();
        services.AddScoped<ConsumerSecretReferenceAdmission>();
        services.AddScoped<AdmitConsumerSecretReferences>(sp =>
            sp.GetRequiredService<ConsumerSecretReferenceAdmission>().InvokeAsync);
        services.AddScoped<ConsumerSecretProjectionResolver>();
        services.AddScoped<EnsureConsumerSecretProjections>(sp =>
            sp.GetRequiredService<ConsumerSecretProjectionResolver>().InvokeAsync);
        services.AddScoped<EnvelopePrepareManagedSecret>();
        services.AddScoped<PrepareManagedSecret>(sp =>
            sp.GetRequiredService<EnvelopePrepareManagedSecret>().InvokeAsync);
        services.AddScoped<EnvelopeReadManagedSecretKey>();
        services.AddScoped<ReadManagedSecretKey>(sp =>
            sp.GetRequiredService<EnvelopeReadManagedSecretKey>().InvokeAsync);
        services.AddScoped<EnvelopeReadManagedSecret>();
        services.AddScoped<ReadManagedSecret>(sp => sp.GetRequiredService<EnvelopeReadManagedSecret>().InvokeAsync);
        services.AddScoped<ReconcileManagedSecretProjection>();
        services.AddScoped<EnsureManagedSecretProjection>(sp =>
            sp.GetRequiredService<ReconcileManagedSecretProjection>().InvokeAsync);
        services.AddScoped<SecretProjectionReconciler>();
        services.AddScoped<SecretProjectionRecoveryJob>();
        services.AddScoped<SecretKeyRetirementReconciler>();
        services.AddScoped<MongoReleaseRetainedSecretKeys>();
        services.AddScoped<ReleaseRetainedSecretKeys>(sp => sp.GetRequiredService<MongoReleaseRetainedSecretKeys>().InvokeAsync);
        services.AddScoped<ISecretProjectionTarget, KubernetesSecretProjectionTarget>();
        return services;
    }

    public static IServiceProvider ScheduleSecretProjectionRecurringJobs(this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<SecretProjectionRecoveryJob>(
            "reconcile-secret-projections",
            job => job.RecoverAsync(CancellationToken.None),
            Cron.Minutely);
        return services;
    }

    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/secrets")
            .WithTags("Secrets")
            .MapCreateSecretEndpoint()
            .MapListSecretsEndpoint()
            .MapGetSecretEndpoint()
            .MapRotateSecretEndpoint()
            .MapDeleteSecretEndpoint()
            .MapBindSecretEndpoint()
            .MapDetachSecretBindingEndpoint()
            .MapGetSecretUsageEndpoint()
            .MapGetSecretProjectionsEndpoint();

        return app;
    }
}
