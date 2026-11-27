// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Application.Runtime.Gateway;
using Pole.Features.Functions.Application.Runtime.Metering;
using Pole.Features.Functions.Application.Runtime.Provisioning;
using Pole.Features.Functions.Application.Runtime.Scaling;
using Pole.Features.Functions.Application.Runtime.Timers;
using Pole.Features.Functions.Application.Usage;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application;

public static class FunctionAppPersistenceRegistration
{
    public static IServiceCollection AddFunctionAppPersistence(this IServiceCollection services)
    {
        services.AddSingletonCollection<FunctionApp>("FunctionApps");
        services.AddSingletonCollection<FunctionRevision>("FunctionRevisions");
        services.AddSingletonCollection<FunctionRouteBinding>("FunctionRouteBindings");
        services.AddSingletonCollection<FunctionTimerState>("FunctionTimerStates");
        services.AddSingletonCollection<FunctionInvocation>("FunctionInvocations");
        services.AddSingletonCollection<FunctionMeterEvent>("FunctionMeterEvents");
        services.AddSingletonCollection<FunctionUsageBucket>("FunctionUsageBuckets");

        services.AddCapability<MongoLoadFunctionAppById, LoadFunctionAppById>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadFunctionAppPage, LoadFunctionAppPage>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadFunctionAppRevisions, LoadFunctionAppRevisions>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<
            MongoLoadFunctionAppRevisionReferences, LoadFunctionAppRevisionReferences>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadFunctionInvocations, LoadFunctionInvocations>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadFunctionUsage, LoadFunctionUsage>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistCreatedFunctionApp, PersistCreatedFunctionApp>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoPersistFunctionAppRevision, PersistFunctionAppRevision>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<FunctionAppDeletionWorkflow, ApplyFunctionAppDeletion>(implementation =>
            implementation.ExecuteAsync);
        services.AddCapability<MongoBeginFunctionAppDeletion, BeginFunctionAppDeletion>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoFinalizeFunctionAppDeletion, FinalizeFunctionAppDeletion>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoRetryFunctionInvocation, RetryFunctionInvocation>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<MongoClaimNextFunctionTimer, ClaimNextFunctionTimer>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoMaterializeFunctionTimerOccurrence, MaterializeFunctionTimerOccurrence>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<MongoReleaseFunctionTimerLease, ReleaseFunctionTimerLease>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoLoadExpiredFunctionInvocations, LoadExpiredFunctionInvocations>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<MongoClaimNextFunctionInvocation, ClaimNextFunctionInvocation>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoLoadFunctionInvocationDispatchSubject, LoadFunctionInvocationDispatchSubject>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoPersistFunctionInvocationTransition, PersistFunctionInvocationTransition>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoLoadUnaggregatedFunctionMeterEvents, LoadUnaggregatedFunctionMeterEvents>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<MongoAggregateFunctionMeterEvent, AggregateFunctionMeterEvent>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<MongoPersistFunctionMeterEvent, PersistFunctionMeterEvent>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<
            MongoLoadFunctionRuntimeRouteTarget, LoadFunctionRuntimeRouteTarget>(implementation =>
            implementation.InvokeAsync);
        services.AddSingletonCapability<MongoLoadFunctionScaleSubjects, LoadFunctionScaleSubjects>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<
            MongoLoadFunctionRevisionProvisioningSubject, LoadFunctionRevisionProvisioningSubject>(implementation =>
            implementation.InvokeAsync);
        services.AddSingleton<RunMongoOutcomeTransaction<PersistRetriedFunctionInvocationOutcome>>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        services.AddSingleton<RunMongoOutcomeTransaction<FunctionRevisionLifecycleOutcome>>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        services.AddSingleton<RunMongoOutcomeTransaction<FinalizeFunctionAppDeletionOutcome>>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        return services;
    }

    private static void AddSingletonCollection<TDocument>(this IServiceCollection services, string name)
        where TDocument : class => services.AddSingleton(serviceProvider =>
        serviceProvider.GetRequiredService<IMongoDatabase>().GetCollection<TDocument>(name));

    private static void AddCapability<TImplementation, TDelegate>(
        this IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.AddScoped<TImplementation>();
        services.AddScoped(serviceProvider => bind(serviceProvider.GetRequiredService<TImplementation>()));
    }

    private static void AddSingletonCapability<TImplementation, TDelegate>(
        this IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.AddSingleton<TImplementation>();
        services.AddSingleton(serviceProvider => bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
