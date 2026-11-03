using Pole.Features.GitOps.Contracts;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Secrets.Application.Persistence;

internal static class SecretPersistenceRegistration
{
    internal static IServiceCollection AddSecretPersistence(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<Secret>("Secrets"));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<SecretVersion>("SecretVersions"));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<SecretProjection>("SecretProjections"));
        services.AddScoped<SecretPlanningFence>();
        services.AddScoped<ConsumerSecretDeletionGuard>();
        services.AddScoped<EnsureConsumerSecretBindingRemoval>(provider =>
            provider.GetRequiredService<ConsumerSecretDeletionGuard>().InvokeAsync);
        services.AddScoped<ManagedSecretAdoption>();
        services.AddScoped<ReadManagedSecretAdoption>(sp =>
            sp.GetRequiredService<ManagedSecretAdoption>().ReadAsync);
        services.AddScoped<FenceManagedSecretAdoption>(sp =>
            sp.GetRequiredService<ManagedSecretAdoption>().FenceAsync);
        services.AddScoped<BootstrapSecrets>();
        services.AddScoped<ReadBootstrapSecret>(sp => sp.GetRequiredService<BootstrapSecrets>().ReadAsync);
        services.AddScoped<CreateBootstrapSecret>(sp => sp.GetRequiredService<BootstrapSecrets>().CreateAsync);
        services.AddScoped<ConsumerSecretPlanningSnapshots>();
        services.AddScoped<LoadConsumerSecretPlanningSnapshot>(sp =>
            sp.GetRequiredService<ConsumerSecretPlanningSnapshots>().ReadAsync);
        services.AddScoped<FenceConsumerSecretPlanningSnapshot>(sp =>
            sp.GetRequiredService<ConsumerSecretPlanningSnapshots>().FenceAsync);
        services.AddScoped<MongoClaimSecretProjection>();
        services.AddScoped<ClaimSecretProjection>(sp => sp.GetRequiredService<MongoClaimSecretProjection>().InvokeAsync);
        services.AddScoped<MongoCompleteSecretProjection>();
        services.AddScoped<CompleteSecretProjection>(sp => sp.GetRequiredService<MongoCompleteSecretProjection>().InvokeAsync);
        services.AddScoped<MongoLoadSecretProjections>();
        services.AddScoped<LoadSecretProjections>(sp => sp.GetRequiredService<MongoLoadSecretProjections>().InvokeAsync);
        services.AddScoped<MongoLoadSecretVersion>();
        services.AddScoped<LoadSecretVersion>(sp => sp.GetRequiredService<MongoLoadSecretVersion>().InvokeAsync);
        services.AddScoped<MongoLoadSecretProjectionCandidates>();
        services.AddScoped<LoadSecretProjectionCandidates>(sp => sp.GetRequiredService<MongoLoadSecretProjectionCandidates>().InvokeAsync);
        services.AddScoped<MongoCreateBoundManagedSecret>();
        services.AddScoped<CreateBoundManagedSecret>(sp => sp.GetRequiredService<MongoCreateBoundManagedSecret>().InvokeAsync);
        services.AddScoped<MongoRotateManagedSecret>();
        services.AddScoped<RotateManagedSecret>(sp => sp.GetRequiredService<MongoRotateManagedSecret>().InvokeAsync);
        services.AddScoped<MongoReleaseManagedSecret>();
        services.AddScoped<ReleaseManagedSecret>(sp => sp.GetRequiredService<MongoReleaseManagedSecret>().InvokeAsync);
        services.AddScoped<MongoLoadManagedSecret>();
        services.AddScoped<LoadManagedSecret>(sp => sp.GetRequiredService<MongoLoadManagedSecret>().InvokeAsync);
        services.AddScoped<MongoLoadSecretById>();
        services.AddScoped<LoadSecretById>(sp => sp.GetRequiredService<MongoLoadSecretById>().InvokeAsync);
        services.AddScoped<MongoLoadSecretPage>();
        services.AddScoped<LoadSecretPage>(sp => sp.GetRequiredService<MongoLoadSecretPage>().InvokeAsync);
        services.AddScoped<MongoPersistCreatedSecret>();
        services.AddScoped<PersistCreatedSecret>(sp => sp.GetRequiredService<MongoPersistCreatedSecret>().InvokeAsync);
        services.AddScoped<MongoPersistRotatedSecret>();
        services.AddScoped<PersistRotatedSecret>(sp => sp.GetRequiredService<MongoPersistRotatedSecret>().InvokeAsync);
        services.AddScoped<MongoPersistSecretBinding>();
        services.AddScoped<PersistSecretBinding>(sp => sp.GetRequiredService<MongoPersistSecretBinding>().InvokeAsync);
        services.AddScoped<MongoRemoveSecretBinding>();
        services.AddScoped<RemoveSecretBinding>(sp => sp.GetRequiredService<MongoRemoveSecretBinding>().InvokeAsync);
        services.AddScoped<MongoDeleteStoredSecret>();
        services.AddScoped<DeleteStoredSecret>(sp => sp.GetRequiredService<MongoDeleteStoredSecret>().InvokeAsync);
        services.AddScoped<MongoRemoveConsumerSecretBindings>();
        services.AddScoped<RemoveConsumerSecretBindings>(sp => sp.GetRequiredService<MongoRemoveConsumerSecretBindings>().InvokeAsync);
        services.AddScoped<OutboxStoreSecretChange>();
        services.AddScoped<StoreSecretChange>(sp => sp.GetRequiredService<OutboxStoreSecretChange>().InvokeAsync);
        services.AddScoped<EnvelopeEncryptSecretVersion>();
        services.AddScoped<EncryptSecretVersion>(sp => sp.GetRequiredService<EnvelopeEncryptSecretVersion>().InvokeAsync);
        services.AddScoped<IIsolationScopeReferenceGuard, SecretScopeReferenceGuard>();
        services.AddScoped<IDeclarativeLifecycleDependencyProvider, SecretLifecycleDependencies>();
        services.AddScoped<DeclarativeSecretProvider>();
        services.AddScoped<IDeclarativeResourceProvider>(sp => sp.GetRequiredService<DeclarativeSecretProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(sp =>
            sp.GetRequiredService<DeclarativeSecretProvider>());
        services.AddScoped<SecretBindingConsumerCatalog>();
        services.AddScoped<SecretBindingPlanning>();
        services.AddScoped<SecretBindingExecution>();
        services.AddScoped<SecretBindingAdoption>();
        services.AddScoped<SecretBindingLifecycle>();
        services.AddScoped<SecretBindingCandidates>();
        services.AddScoped<DeclarativeSecretBindingProvider>();
        services.AddScoped<IDeclarativeResourceProvider>(sp =>
            sp.GetRequiredService<DeclarativeSecretBindingProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(sp =>
            sp.GetRequiredService<DeclarativeSecretBindingProvider>());
        return services;
    }
}
