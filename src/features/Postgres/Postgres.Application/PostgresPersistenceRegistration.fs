namespace Pole.Features.Postgres.Application

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Application.Credentials
open Pole.Features.Postgres.Application.Lifecycle
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceConnections.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Secrets.Contracts
open Pole.Features.Secrets.Primitives
open Pole.Features.GitOps.Contracts
open Pole.Features.Postgres.Application.Declarations

[<RequireQualifiedAccess>]
module PostgresPersistenceRegistration =
    let addPostgresPersistence (services : IServiceCollection) =
        services.TryAddSingleton<IMongoCollection<PostgresCluster>>(
            Func<IServiceProvider, IMongoCollection<PostgresCluster>>(fun serviceProvider ->
                serviceProvider
                    .GetRequiredService<IMongoDatabase>()
                    .GetCollection<PostgresCluster>("Postgres")))
        |> ignore
        services.AddScoped<IResourceConnectionDestinationCapability, MongoLoadPostgresConnectionEndpoints>()
        |> ignore
        services.AddScoped<IResourceConnectionDestinationNameCapability, MongoLoadPostgresConnectionEndpoints>()
        |> ignore
        services.AddScoped<MongoLoadPostgresClusterById>() |> ignore
        services.AddScoped<LoadPostgresClusterById>(
            Func<IServiceProvider, LoadPostgresClusterById>(fun serviceProvider ->
                LoadPostgresClusterById(
                    fun tenantId clusterId cancellationToken ->
                        serviceProvider.GetRequiredService<MongoLoadPostgresClusterById>()
                            .InvokeAsync(tenantId, clusterId, cancellationToken))))
        |> ignore
        services.AddScoped<MongoLoadPostgresClusterPage>() |> ignore
        services.AddScoped<LoadPostgresClusterPage>(
            Func<IServiceProvider, LoadPostgresClusterPage>(fun serviceProvider ->
                LoadPostgresClusterPage(
                    fun query cancellationToken ->
                        serviceProvider.GetRequiredService<MongoLoadPostgresClusterPage>()
                            .InvokeAsync(query, cancellationToken))))
        |> ignore
        services.AddScoped<MongoPersistCreatedPostgresCluster>() |> ignore
        services.AddScoped<PersistCreatedPostgresCluster>(
            Func<IServiceProvider, PersistCreatedPostgresCluster>(fun serviceProvider ->
                PersistCreatedPostgresCluster(
                    fun session cluster cancellationToken ->
                        serviceProvider.GetRequiredService<MongoPersistCreatedPostgresCluster>()
                            .InvokeAsync(session, cluster, cancellationToken))))
        |> ignore
        services.AddScoped<MongoPersistUpdatedPostgresCluster>() |> ignore
        services.AddScoped<PersistUpdatedPostgresCluster>(
            Func<IServiceProvider, PersistUpdatedPostgresCluster>(fun serviceProvider ->
                PersistUpdatedPostgresCluster(
                    fun request session cancellationToken ->
                        serviceProvider.GetRequiredService<MongoPersistUpdatedPostgresCluster>()
                            .InvokeAsync(request, session, cancellationToken))))
        |> ignore
        services.AddScoped<MongoBeginPostgresClusterDeletion>() |> ignore
        services.AddScoped<BeginPostgresClusterDeletion>(
            Func<IServiceProvider, BeginPostgresClusterDeletion>(fun serviceProvider ->
                BeginPostgresClusterDeletion(
                    fun request session cancellationToken ->
                        serviceProvider.GetRequiredService<MongoBeginPostgresClusterDeletion>()
                            .InvokeAsync(request, session, cancellationToken))))
        |> ignore
        services.AddScoped<MongoFinalizePostgresClusterDeletion>() |> ignore
        services.AddScoped<FinalizePostgresClusterDeletion>(
            Func<IServiceProvider, FinalizePostgresClusterDeletion>(fun serviceProvider ->
                FinalizePostgresClusterDeletion(
                    fun tenantId clusterId cancellationToken ->
                        serviceProvider.GetRequiredService<MongoFinalizePostgresClusterDeletion>()
                            .InvokeAsync(tenantId, clusterId, cancellationToken))))
        |> ignore
        services.AddSingleton(SecretDeclarationConsumer(
            "PostgresCluster", PostgresResourceDeployment.ResourceType, SecretBindingUsage.Bootstrap)) |> ignore
        services.AddScoped<PostgresBootstrapInitializer>() |> ignore
        services.AddScoped<PostgresPlanningReader>() |> ignore
        services.AddScoped<PostgresDeclarationPlanner>() |> ignore
        services.AddScoped<PostgresDeclarationReferences>() |> ignore
        services.AddScoped<PostgresDeclarationExecution>() |> ignore
        services.AddScoped<PostgresDeclarationAdoption>() |> ignore
        services.AddScoped<PostgresDeclarationLifecycle>() |> ignore
        services.AddScoped<DeclarativePostgresProvider>() |> ignore
        services.AddScoped<IDeclarativeResourceProvider>(Func<IServiceProvider, IDeclarativeResourceProvider>(fun sp ->
            sp.GetRequiredService<DeclarativePostgresProvider>())) |> ignore
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(
            Func<IServiceProvider, IDeclarativeResourceLifecycleProvider>(fun sp ->
                sp.GetRequiredService<DeclarativePostgresProvider>())) |> ignore
        services.AddScoped<IDeclarativeLifecycleDependencyProvider>(
            Func<IServiceProvider, IDeclarativeLifecycleDependencyProvider>(fun sp ->
                sp.GetRequiredService<DeclarativePostgresProvider>())) |> ignore
        services.AddScoped<PostgresCredentialCreator>() |> ignore
        services.AddScoped<CreatePostgresCredential>(
            Func<IServiceProvider, CreatePostgresCredential>(fun serviceProvider ->
                CreatePostgresCredential(
                    fun tenantId clusterId username password cancellationToken ->
                        serviceProvider.GetRequiredService<PostgresCredentialCreator>()
                            .InvokeAsync(tenantId, clusterId, username, password, cancellationToken))))
        |> ignore
        services.AddScoped<ManagedLoadPostgresCredential>() |> ignore
        services.AddScoped<LoadPostgresCredential>(
            Func<IServiceProvider, LoadPostgresCredential>(fun serviceProvider ->
                LoadPostgresCredential(
                    fun tenantId clusterId cancellationToken ->
                        serviceProvider.GetRequiredService<ManagedLoadPostgresCredential>()
                            .InvokeAsync(tenantId, clusterId, cancellationToken))))
        |> ignore
        services.AddScoped<PostgresCredentialRotator>() |> ignore
        services.AddScoped<RotatePostgresCredential>(
            Func<IServiceProvider, RotatePostgresCredential>(fun serviceProvider ->
                RotatePostgresCredential(
                    fun tenantId clusterId credential password cancellationToken ->
                        serviceProvider.GetRequiredService<PostgresCredentialRotator>()
                            .InvokeAsync(tenantId, clusterId, credential, password, cancellationToken))))
        |> ignore
        services.AddScoped<ManagedPersistRotatedPostgresCredential>() |> ignore
        services.AddScoped<PersistRotatedPostgresCredential>(
            Func<IServiceProvider, PersistRotatedPostgresCredential>(fun serviceProvider ->
                PersistRotatedPostgresCredential(
                    fun session current replacement cancellationToken ->
                        serviceProvider.GetRequiredService<ManagedPersistRotatedPostgresCredential>()
                            .InvokeAsync(session, current, replacement, cancellationToken))))
        |> ignore
        services.AddScoped<ManagedResolvePostgresClusterCredentials>() |> ignore
        services.AddScoped<ResolvePostgresClusterCredentials>(
            Func<IServiceProvider, ResolvePostgresClusterCredentials>(fun serviceProvider ->
                ResolvePostgresClusterCredentials(
                    fun tenantId clusterId cancellationToken ->
                        serviceProvider.GetRequiredService<ManagedResolvePostgresClusterCredentials>()
                            .InvokeAsync(tenantId, clusterId, cancellationToken))))
        |> ignore

        services.TryAddScoped<RunMongoOutcomeTransaction<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>>(
            Func<IServiceProvider, RunMongoOutcomeTransaction<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>>(
                fun serviceProvider ->
                    RunMongoOutcomeTransaction<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>(
                        fun operation cancellationToken ->
                            serviceProvider
                                .GetRequiredService<MongoRunTransaction>()
                                .InvokeOutcomeAsync<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>(
                                    operation,
                                    cancellationToken))))
        |> ignore
        services
