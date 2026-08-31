namespace Pole.Features.Postgres.Application

open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Options
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Endpoints
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Quotas.Contracts

[<AbstractClass; Sealed>]
type PostgresFeatureRegistration =
    [<System.Runtime.CompilerServices.Extension>]
    static member AddPostgresFeature(
        services : IServiceCollection,
        configuration : IConfiguration) =
        services
            .AddOptions<PostgresSettings>()
            .Bind(configuration.GetSection(PostgresSettings.SectionName))
        |> ignore
        services.TryAddSingleton<PostgresSettings>(
            Func<IServiceProvider, PostgresSettings>(fun serviceProvider ->
                serviceProvider.GetRequiredService<IOptions<PostgresSettings>>().Value))
        |> ignore
        services.TryAddSingleton<PostgresClusterCountQuota>() |> ignore
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Quota, PostgresClusterCountQuota>())
        |> ignore
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IQuotaProfile, PostgresQuotaProfile>())
        |> ignore
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
