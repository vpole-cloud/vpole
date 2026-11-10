namespace Pole.Features.Search.Application.Common

open System
open System.Collections.Generic
open Pole.Features.Search.Contracts
open Pole.Workloads

type SearchSourceTypeRegistry(sourceTypes: IEnumerable<SearchSourceType>) =
    let registered = sourceTypes |> Seq.toArray

    do
        registered
        |> Array.iter (fun sourceType ->
            if String.IsNullOrWhiteSpace(sourceType.ApiName) then
                invalidArg (nameof sourceTypes) "A search source API name is required.")

        registered
        |> Array.groupBy _.ResourceTypeId
        |> Array.tryFind (fun (_, values) -> values.Length > 1)
        |> Option.iter (fun (resourceTypeId, _) ->
            invalidArg (nameof sourceTypes) $"Search resource type '{resourceTypeId}' is registered more than once.")

        registered
        |> Array.groupBy (fun value -> value.ApiName.ToUpperInvariant())
        |> Array.tryFind (fun (_, values) -> values.Length > 1)
        |> Option.iter (fun (_, values) ->
            invalidArg (nameof sourceTypes) $"Search API type '{values[0].ApiName}' is registered more than once.")

    member _.Resolve(resourceTypeId: ResourceTypeId) =
        registered |> Array.tryFind (fun value -> value.ResourceTypeId = resourceTypeId)

    member _.ResolveApiName(apiName: string) =
        registered
        |> Array.tryFind (fun value -> String.Equals(value.ApiName, apiName, StringComparison.OrdinalIgnoreCase))
