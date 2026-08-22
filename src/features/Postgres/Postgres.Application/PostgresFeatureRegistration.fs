namespace Pole.Features.Postgres.Application

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Endpoints
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Features.Postgres.Application.Persistence

[<AbstractClass; Sealed>]
type PostgresFeatureRegistration =
    [<System.Runtime.CompilerServices.Extension>]
    static member AddPostgresFeature(services : IServiceCollection) =
        PostgresPersistenceRegistration.addPostgresPersistence services |> ignore
        HttpProblemMappingServiceCollectionExtensions.AddHttpProblemMappings(
            services,
            PostgresHttpMappings.all)
        |> ignore
        services.TryAddScoped<RunMongoOutcomeTransaction<UpdatePostgresClusterPasswordOutcome>>(
            Func<IServiceProvider, RunMongoOutcomeTransaction<UpdatePostgresClusterPasswordOutcome>>(
                fun serviceProvider ->
                    RunMongoOutcomeTransaction<UpdatePostgresClusterPasswordOutcome>(
                        fun operation cancellationToken ->
                            serviceProvider
                                .GetRequiredService<MongoRunTransaction>()
                                .InvokeOutcomeAsync<UpdatePostgresClusterPasswordOutcome>(
                                    operation,
                                    cancellationToken))))
        |> ignore
        services.TryAddScoped<PostgresResourceDeploymentAdapter>() |> ignore
        services.TryAddSingleton<IPostgresClusterDeploymentManager, PostgresClusterDeploymentManager>()
        |> ignore
        services.TryAddSingleton(PostgresResourceDeploymentAdapterRegistration.Descriptor)
        |> ignore
        services
