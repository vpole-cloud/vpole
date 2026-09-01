namespace Pole.Features.Search.Application

open System
open Microsoft.Extensions.DependencyInjection
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Application.Persistence
open Pole.Features.Search.Contracts

[<AbstractClass; Sealed>]
type SearchFeatureRegistration =
    [<System.Runtime.CompilerServices.Extension>]
    static member AddSearchFeature(services : IServiceCollection) =
        services.AddSingleton<SearchSourceTypeRegistry>() |> ignore
        services.AddSingleton<ResolveSearchSourceType>(
            Func<IServiceProvider, ResolveSearchSourceType>(fun serviceProvider ->
                ResolveSearchSourceType(fun resourceTypeId ->
                    serviceProvider.GetRequiredService<SearchSourceTypeRegistry>()
                        .Resolve(resourceTypeId)
                    |> Option.toObj)))
        |> ignore

        services.AddScoped<SearchProjectionStore>() |> ignore
        services.AddScoped<SearchProjectionWriter>() |> ignore
        services.AddScoped<CreateSearchProjection>(
            Func<IServiceProvider, CreateSearchProjection>(fun serviceProvider ->
                CreateSearchProjection(fun snapshot cancellationToken ->
                    serviceProvider.GetRequiredService<SearchProjectionWriter>()
                        .UpsertAsync(snapshot, cancellationToken))))
        |> ignore
        services.AddScoped<UpdateSearchProjection>(
            Func<IServiceProvider, UpdateSearchProjection>(fun serviceProvider ->
                UpdateSearchProjection(fun snapshot cancellationToken ->
                    serviceProvider.GetRequiredService<SearchProjectionWriter>()
                        .UpsertAsync(snapshot, cancellationToken))))
        |> ignore

        services.AddScoped<MongoDeleteSearchProjection>() |> ignore
        services.AddScoped<DeleteSearchProjection>(
            Func<IServiceProvider, DeleteSearchProjection>(fun serviceProvider ->
                DeleteSearchProjection(fun identity resourceRevision cancellationToken ->
                    serviceProvider.GetRequiredService<MongoDeleteSearchProjection>()
                        .InvokeAsync(identity, resourceRevision, cancellationToken))))
        |> ignore

        services.AddScoped<MongoUpdateSearchOperationalStatus>() |> ignore
        services.AddScoped<UpdateSearchOperationalStatus>(
            Func<IServiceProvider, UpdateSearchOperationalStatus>(fun serviceProvider ->
                UpdateSearchOperationalStatus(fun identity status cancellationToken ->
                    serviceProvider.GetRequiredService<MongoUpdateSearchOperationalStatus>()
                        .InvokeAsync(identity, status, cancellationToken))))
        |> ignore

        services.AddScoped<MongoSearchIndexedDocuments>() |> ignore
        services.AddScoped<SearchIndexedDocuments>(
            Func<IServiceProvider, SearchIndexedDocuments>(fun serviceProvider ->
                SearchIndexedDocuments(fun query cancellationToken ->
                    serviceProvider.GetRequiredService<MongoSearchIndexedDocuments>()
                        .InvokeAsync(query, cancellationToken))))
        |> ignore
        services
