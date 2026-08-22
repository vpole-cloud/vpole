using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Application.Persistence.Mutations;
using Pole.Features.Functions.Application.Persistence.Reads;
using Pole.Features.Functions.Application.Persistence.Contracts;
using Pole.Features.Functions.Application.Persistence.Runtime.Gateway;
using Pole.Features.Functions.Application.Persistence.Runtime.Metering;
using Pole.Features.Functions.Application.Persistence.Runtime.Provisioning;
using Pole.Features.Functions.Application.Persistence.Runtime.Scaling;
using Pole.Features.Functions.Application.Persistence.Runtime.Timers;
using Pole.Features.Functions.Domain;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Persistence;

public static class FunctionAppPersistenceRegistration
{
    public static IServiceCollection AddFunctionAppPersistence(
        this IServiceCollection services)
    {
        services.AddSingletonCollection<FunctionApp>("FunctionApps");
        services.AddSingletonCollection<FunctionRevision>("FunctionRevisions");
        services.AddSingletonCollection<FunctionRouteBinding>("FunctionRouteBindings");
        services.AddSingletonCollection<FunctionTimerState>("FunctionTimerStates");
        services.AddSingletonCollection<FunctionInvocation>("FunctionInvocations");
        services.AddSingletonCollection<FunctionMeterEvent>("FunctionMeterEvents");
        services.AddSingletonCollection<FunctionUsageBucket>("FunctionUsageBuckets");

        services.AddCapability<MongoLoadFunctionAppById, LoadFunctionAppById>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadFunctionAppPage, LoadFunctionAppPage>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadFunctionAppRevisions, LoadFunctionAppRevisions>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<
            MongoLoadFunctionAppRevisionReferences,
            LoadFunctionAppRevisionReferences>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadFunctionInvocations, LoadFunctionInvocations>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadFunctionUsage, LoadFunctionUsage>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistCreatedFunctionApp, PersistCreatedFunctionApp>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistFunctionAppRevision, PersistFunctionAppRevision>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoBeginFunctionAppDeletion, BeginFunctionAppDeletion>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoRetryFunctionInvocation, RetryFunctionInvocation>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<
            ResourceGroupPlacementResolutionAdapter,
            ResolveFunctionAppResourceGroupPlacement>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<MongoClaimNextFunctionTimer, ClaimNextFunctionTimer>(
            implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoMaterializeFunctionTimerOccurrence,
            MaterializeFunctionTimerOccurrence>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<MongoReleaseFunctionTimerLease, ReleaseFunctionTimerLease>(
            implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoLoadExpiredFunctionInvocations,
            LoadExpiredFunctionInvocations>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoClaimNextFunctionInvocation,
            ClaimNextFunctionInvocation>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoLoadFunctionInvocationDispatchSubject,
            LoadFunctionInvocationDispatchSubject>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoPersistFunctionInvocationTransition,
            PersistFunctionInvocationTransition>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoLoadUnaggregatedFunctionMeterEvents,
            LoadUnaggregatedFunctionMeterEvents>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoAggregateFunctionMeterEvent,
            AggregateFunctionMeterEvent>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoPersistFunctionMeterEvent,
            PersistFunctionMeterEvent>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoLoadFunctionRuntimeRouteTarget,
            LoadFunctionRuntimeRouteTarget>(implementation => implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoLoadFunctionScaleSubjects,
            LoadFunctionScaleSubjects>(implementation => implementation.InvokeAsync);
        services.AddCapability<
            MongoLoadFunctionRevisionProvisioningSubject,
            LoadFunctionRevisionProvisioningSubject>(implementation => implementation.InvokeAsync);
        services.AddSingleton<RunMongoOutcomeTransaction<Result<
            FunctionApp,
            BeginFunctionAppDeletionError>>>(serviceProvider => serviceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<Result<FunctionApp, BeginFunctionAppDeletionError>>);
        services.AddSingleton<RunMongoOutcomeTransaction<Result<
            FunctionInvocationManualRetry,
            RetryFunctionInvocationError>>>(serviceProvider => serviceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<Result<
                FunctionInvocationManualRetry,
                RetryFunctionInvocationError>>);
        return services;
    }

    private static void AddSingletonCollection<TDocument>(
        this IServiceCollection services,
        string name)
        where TDocument : class =>
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<TDocument>(name));

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

    private static void AddSingletonCapability<TImplementation, TDelegate>(
        this IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.AddSingleton<TImplementation>();
        services.AddSingleton(serviceProvider =>
            bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
