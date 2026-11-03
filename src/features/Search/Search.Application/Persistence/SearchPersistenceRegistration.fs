namespace Pole.Features.Search.Application.Persistence

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open MongoDB.Driver
open Pole.Features.Search.Domain

[<RequireQualifiedAccess>]
module internal SearchPersistenceRegistration =
    let registerCollections (services : IServiceCollection) =
        services.TryAddSingleton<IMongoCollection<SearchDocument>>(
            Func<IServiceProvider, IMongoCollection<SearchDocument>>(fun sp ->
                sp.GetRequiredService<IMongoDatabase>().GetCollection<SearchDocument>("SearchDocuments")))
        |> ignore
        services.TryAddSingleton<IMongoCollection<SearchSourceDocument>>(
            Func<IServiceProvider, IMongoCollection<SearchSourceDocument>>(fun sp ->
                sp.GetRequiredService<IMongoDatabase>().GetCollection<SearchSourceDocument>("SearchSourceDocuments")))
        |> ignore
        services.TryAddSingleton<IMongoCollection<SearchProjectionVersionDocument>>(
            Func<IServiceProvider, IMongoCollection<SearchProjectionVersionDocument>>(fun sp ->
                sp.GetRequiredService<IMongoDatabase>().GetCollection<SearchProjectionVersionDocument>("SearchProjectionVersions")))
        |> ignore
        services.AddSingleton(RealtimeTopic("search", RealtimeScope.Tenant)) |> ignore
        services.AddSingleton<SearchSourceTypeRegistry>() |> ignore
