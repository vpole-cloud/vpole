namespace Pole.Features.Search.Application

open System
open Microsoft.Extensions.DependencyInjection
open Pole.Features.Search.Application.Persistence

[<AbstractClass; Sealed>]
type SearchFeatureRegistration =
    [<System.Runtime.CompilerServices.Extension>]
    static member AddSearchFeature(services : IServiceCollection) =
        services.AddScoped<SearchProjectionStore>() |> ignore
        services.AddScoped<MongoPersistSearchDocumentSet>() |> ignore
        services.AddScoped<PersistSearchDocumentSet>(
            Func<IServiceProvider, PersistSearchDocumentSet>(fun serviceProvider ->
                PersistSearchDocumentSet(
                    fun documentSet cancellationToken ->
                        serviceProvider.GetRequiredService<MongoPersistSearchDocumentSet>()
                            .InvokeAsync(documentSet, cancellationToken))))
        |> ignore
        services.AddScoped<MongoRemoveSearchDocumentSet>() |> ignore
        services.AddScoped<RemoveSearchDocumentSet>(
            Func<IServiceProvider, RemoveSearchDocumentSet>(fun serviceProvider ->
                RemoveSearchDocumentSet(
                    fun sourceType tenantId sourceId cancellationToken ->
                        serviceProvider.GetRequiredService<MongoRemoveSearchDocumentSet>()
                            .InvokeAsync(sourceType, tenantId, sourceId, cancellationToken))))
        |> ignore
        services.AddScoped<MongoPersistSearchOperationalStatus>() |> ignore
        services.AddScoped<PersistSearchOperationalStatus>(
            Func<IServiceProvider, PersistSearchOperationalStatus>(fun serviceProvider ->
                PersistSearchOperationalStatus(
                    fun sourceType tenantId sourceId status eventOccurredAtUtc cancellationToken ->
                        serviceProvider.GetRequiredService<MongoPersistSearchOperationalStatus>()
                            .InvokeAsync(
                                sourceType,
                                tenantId,
                                sourceId,
                                status,
                                eventOccurredAtUtc,
                                cancellationToken))))
        |> ignore
        services.AddScoped<MongoSearchIndexedDocuments>() |> ignore
        services.AddScoped<SearchIndexedDocuments>(
            Func<IServiceProvider, SearchIndexedDocuments>(fun serviceProvider ->
                SearchIndexedDocuments(
                    fun query cancellationToken ->
                        serviceProvider.GetRequiredService<MongoSearchIndexedDocuments>()
                            .InvokeAsync(query, cancellationToken))))
        |> ignore
        services
