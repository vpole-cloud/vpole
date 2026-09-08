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
open Pole.Features.Postgres.Application.Search
open Pole.Features.Postgres.Domain
open Pole.Features.Quotas.Contracts
open Pole.Features.Search.Contracts

[<AbstractClass; Sealed>]
type PostgresFeatureRegistration =
    [<System.Runtime.CompilerServices.Extension>]
    static member AddPostgresFeature(
        services : IServiceCollection,
        configuration : IConfiguration) =
        services.AddSingleton<SearchSourceType>(PostgresSearchProjection.SourceType)
        |> ignore
        services
            .AddOptions<PostgresSettings>()
            .Bind(configuration.GetSection(PostgresSettings.SectionName))
            .Validate(
                Func<PostgresSettings, bool>(fun settings ->
                    settings.SupportedVersions.From > 0 &&
                    settings.SupportedVersions.``To`` >= settings.SupportedVersions.From &&
                    Enum.IsDefined(typeof<PostgresDeploymentMode>, box settings.DefaultDeploymentMode) &&
                    not (String.IsNullOrWhiteSpace(settings.OperatorImage.Server)) &&
                    not (String.IsNullOrWhiteSpace(settings.OperatorImage.Name)) &&
                    not (String.IsNullOrWhiteSpace(settings.StatefulSetImage.Server)) &&
                    not (String.IsNullOrWhiteSpace(settings.StatefulSetImage.Name))),
                "PostgreSQL requires a valid deployment mode, ordered positive version bounds, and nonblank image server/name values.")
            .ValidateOnStart()
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
