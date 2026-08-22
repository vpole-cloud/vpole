namespace Pole.Features.Postgres.Application.Persistence

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Application.Persistence.Credentials
open Pole.Features.Postgres.Application.Persistence.Lifecycle
open Pole.Features.Postgres.Application.Persistence.Mutations
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.Postgres.Domain

[<RequireQualifiedAccess>]
module PostgresPersistenceRegistration =
    let addPostgresPersistence (services : IServiceCollection) =
        services.TryAddSingleton<IMongoCollection<PostgresCluster>>(
            Func<IServiceProvider, IMongoCollection<PostgresCluster>>(fun serviceProvider ->
                serviceProvider
                    .GetRequiredService<IMongoDatabase>()
                    .GetCollection<PostgresCluster>("Postgres")))
        |> ignore
        services.TryAddSingleton<IMongoCollection<PostgresCredential>>(
            Func<IServiceProvider, IMongoCollection<PostgresCredential>>(fun serviceProvider ->
                serviceProvider
                    .GetRequiredService<IMongoDatabase>()
                    .GetCollection<PostgresCredential>(PostgresCredential.CollectionName)))
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
                    fun session cluster credential cancellationToken ->
                        serviceProvider.GetRequiredService<MongoPersistCreatedPostgresCluster>()
                            .InvokeAsync(session, cluster, credential, cancellationToken))))
        |> ignore
        services.AddScoped<MongoPersistUpdatedPostgresCluster>() |> ignore
        services.AddScoped<PersistUpdatedPostgresCluster>(
            Func<IServiceProvider, PersistUpdatedPostgresCluster>(fun serviceProvider ->
                PersistUpdatedPostgresCluster(
                    fun session current replacement cancellationToken ->
                        serviceProvider.GetRequiredService<MongoPersistUpdatedPostgresCluster>()
                            .InvokeAsync(session, current, replacement, cancellationToken))))
        |> ignore
        services.AddScoped<MongoBeginPostgresClusterDeletion>() |> ignore
        services.AddScoped<BeginPostgresClusterDeletion>(
            Func<IServiceProvider, BeginPostgresClusterDeletion>(fun serviceProvider ->
                BeginPostgresClusterDeletion(
                    fun session current replacement cancellationToken ->
                        serviceProvider.GetRequiredService<MongoBeginPostgresClusterDeletion>()
                            .InvokeAsync(session, current, replacement, cancellationToken))))
        |> ignore
        services.AddScoped<MongoFinalizePostgresClusterDeletion>() |> ignore
        services.AddScoped<FinalizePostgresClusterDeletion>(
            Func<IServiceProvider, FinalizePostgresClusterDeletion>(fun serviceProvider ->
                FinalizePostgresClusterDeletion(
                    fun tenantId clusterId cancellationToken ->
                        serviceProvider.GetRequiredService<MongoFinalizePostgresClusterDeletion>()
                            .InvokeAsync(tenantId, clusterId, cancellationToken))))
        |> ignore
        services.AddScoped<PostgresCredentialCreator>() |> ignore
        services.AddScoped<CreatePostgresCredential>(
            Func<IServiceProvider, CreatePostgresCredential>(fun serviceProvider ->
                CreatePostgresCredential(
                    fun tenantId clusterId username password cancellationToken ->
                        serviceProvider.GetRequiredService<PostgresCredentialCreator>()
                            .InvokeAsync(tenantId, clusterId, username, password, cancellationToken))))
        |> ignore
        services.AddScoped<MongoLoadPostgresCredential>() |> ignore
        services.AddScoped<LoadPostgresCredential>(
            Func<IServiceProvider, LoadPostgresCredential>(fun serviceProvider ->
                LoadPostgresCredential(
                    fun tenantId clusterId cancellationToken ->
                        serviceProvider.GetRequiredService<MongoLoadPostgresCredential>()
                            .InvokeAsync(tenantId, clusterId, cancellationToken))))
        |> ignore
        services.AddScoped<PostgresCredentialRotator>() |> ignore
        services.AddScoped<RotatePostgresCredential>(
            Func<IServiceProvider, RotatePostgresCredential>(fun serviceProvider ->
                RotatePostgresCredential(
                    fun credential password cancellationToken ->
                        serviceProvider.GetRequiredService<PostgresCredentialRotator>()
                            .InvokeAsync(credential, password, cancellationToken))))
        |> ignore
        services.AddScoped<MongoPersistRotatedPostgresCredential>() |> ignore
        services.AddScoped<PersistRotatedPostgresCredential>(
            Func<IServiceProvider, PersistRotatedPostgresCredential>(fun serviceProvider ->
                PersistRotatedPostgresCredential(
                    fun session current replacement cancellationToken ->
                        serviceProvider.GetRequiredService<MongoPersistRotatedPostgresCredential>()
                            .InvokeAsync(session, current, replacement, cancellationToken))))
        |> ignore
        services.AddScoped<MongoResolvePostgresClusterCredentials>() |> ignore
        services.AddScoped<ResolvePostgresClusterCredentials>(
            Func<IServiceProvider, ResolvePostgresClusterCredentials>(fun serviceProvider ->
                ResolvePostgresClusterCredentials(
                    fun tenantId clusterId cancellationToken ->
                        serviceProvider.GetRequiredService<MongoResolvePostgresClusterCredentials>()
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
