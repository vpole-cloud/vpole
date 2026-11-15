using Hangfire;
using Microsoft.Extensions.Configuration;
using Pole.Features.SeaweedFS.Application.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.Quotas.Contracts;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Application.Kubernetes;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application;

public static class SeaweedFSFeatureRegistration
{
    public static IServiceCollection AddSeaweedFSFeature(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
            sp.GetRequiredService<IMongoDatabase>().GetCollection<SeaweedFSService>("SeaweedFS"));
        services.TryAddSingleton(sp =>
            sp.GetRequiredService<IMongoDatabase>().GetCollection<SeaweedFSBucket>("SeaweedFSBuckets"));
        services.AddScoped<MongoSeaweedFSReads>();
        services.AddScoped<LoadSeaweedFSById>(sp => sp.GetRequiredService<MongoSeaweedFSReads>().LoadAsync);
        services.AddScoped<LoadSeaweedFSBuckets>(sp => sp.GetRequiredService<MongoSeaweedFSReads>().BucketsAsync);
        services.AddScoped<LoadSeaweedFSBucket>(sp => sp.GetRequiredService<MongoSeaweedFSReads>().BucketAsync);
        services.AddScoped<MongoPersistCreatedSeaweedFS>();
        services.AddScoped<PersistCreatedSeaweedFS>(sp =>
            sp.GetRequiredService<MongoPersistCreatedSeaweedFS>().InvokeAsync);
        services.TryAddSingleton<SeaweedFSServiceCountQuota>();
        services.TryAddSingleton<SeaweedFSStorageGbQuota>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Quota, SeaweedFSServiceCountQuota>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Quota, SeaweedFSStorageGbQuota>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IQuotaProfile, SeaweedFSQuotaProfile>());
        services.AddScoped<SeaweedFSMutations>();
        services.AddScoped<SeaweedFSQueries>();
        services.AddScoped<SeaweedFSAudit>();
        services.AddScoped<SeaweedFSRetentionJob>();
        services.AddOptions<SeaweedFSSettings>()
            .Configure<IConfiguration>((options, configuration) => configuration.GetSection("SeaweedFS").Bind(options))
            .Validate(
                options => options.DeletionRetention > TimeSpan.Zero,
                "SeaweedFS deletion retention must be positive.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSeaweedFSRuntime();
        services.AddSeaweedFSConnections();
        return services;
    }

    public static IServiceProvider ScheduleSeaweedFSRecurringJobs(this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<SeaweedFSRetentionJob>(
            "seaweedfs-retention",
            job => job.ProcessAsync(CancellationToken.None),
            Cron.Minutely());
        return services;
    }
}
