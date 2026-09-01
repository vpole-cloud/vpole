namespace Pole.Features.Search.Application.Projection

open System.Text.RegularExpressions
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Contracts
open Pole.Features.Search.Domain

[<RequireQualifiedAccess>]
module SearchDocumentFactory =
    [<Literal>]
    let IdWeight = 1000000
    [<Literal>]
    let NameWeight = 100000
    [<Literal>]
    let NameTokenWeight = 50000
    [<Literal>]
    let DomainWeight = 80000
    [<Literal>]
    let DomainTokenWeight = 40000
    [<Literal>]
    let TagWeight = 10000
    [<Literal>]
    let TagTokenWeight = 5000
    [<Literal>]
    let ImageWeight = 8000
    [<Literal>]
    let ImageTokenWeight = 4000
    [<Literal>]
    let ResourceGroupNameWeight = 5000
    [<Literal>]
    let ResourceGroupNameTokenWeight = 2500
    [<Literal>]
    let StorageClassNameWeight = 6000
    [<Literal>]
    let StorageClassNameTokenWeight = 3000
    [<Literal>]
    let ResourceNameWeight = 7000
    [<Literal>]
    let ResourceNameTokenWeight = 3500
    [<Literal>]
    let GroupIdWeight = 6000
    [<Literal>]
    let GroupIdTokenWeight = 3000

    type private SearchValueSpec =
        { KeyType : SearchKeyType
          Value : string
          FullWeight : int
          TokenWeight : int option }

    let normalize (value : string) =
        if System.String.IsNullOrWhiteSpace(value) then ""
        else value.Trim().ToLowerInvariant()

    let tokenize (value : string) =
        normalize value
        |> fun normalized -> Regex.Split(normalized, "[^a-z0-9]+")
        |> Array.map _.Trim()
        |> Array.filter (fun token -> token.Length > 1)
        |> Array.distinct
        |> Array.toList

    let private weights fieldType =
        match fieldType with
        | SearchProjectionFieldType.SourceId -> SearchKeyType.SourceId, IdWeight, None
        | SearchProjectionFieldType.Name -> SearchKeyType.Name, NameWeight, Some NameTokenWeight
        | SearchProjectionFieldType.Tag -> SearchKeyType.Tag, TagWeight, Some TagTokenWeight
        | SearchProjectionFieldType.Domain -> SearchKeyType.Domain, DomainWeight, Some DomainTokenWeight
        | SearchProjectionFieldType.Image -> SearchKeyType.Image, ImageWeight, Some ImageTokenWeight
        | SearchProjectionFieldType.ResourceGroupName -> SearchKeyType.ResourceGroupName, ResourceGroupNameWeight, Some ResourceGroupNameTokenWeight
        | SearchProjectionFieldType.StorageClassName -> SearchKeyType.StorageClassName, StorageClassNameWeight, Some StorageClassNameTokenWeight
        | SearchProjectionFieldType.ResourceName -> SearchKeyType.ResourceName, ResourceNameWeight, Some ResourceNameTokenWeight
        | SearchProjectionFieldType.Username -> SearchKeyType.Username, NameWeight, Some NameTokenWeight
        | SearchProjectionFieldType.GroupId -> SearchKeyType.GroupId, GroupIdWeight, Some GroupIdTokenWeight
        | value -> invalidArg (nameof fieldType) $"Unsupported search projection field type '{value}'."

    let private createDocument (sourceType : SearchSourceType) tenantId sourceId title resourceGroupId resourceGroupName keyType weight searchValue =
        { Id = SearchDocument.createId()
          ResourceTypeId = sourceType.ResourceTypeId.Value
          SourceApiName = sourceType.ApiName
          SourceSortOrder = sourceType.SortOrder
          TenantId = tenantId
          SourceId = sourceId
          KeyType = keyType
          SearchValue = searchValue
          Weight = weight
          Title = title
          ResourceGroupId = resourceGroupId
          ResourceGroupName = resourceGroupName }

    let private toDocuments (sourceType : SearchSourceType) tenantId sourceId title resourceGroupId resourceGroupName specs =
        specs
        |> List.collect (fun spec ->
            let normalizedValue = normalize spec.Value
            if System.String.IsNullOrWhiteSpace(normalizedValue) then []
            else
                let fullDocument =
                    createDocument sourceType tenantId sourceId title resourceGroupId resourceGroupName spec.KeyType spec.FullWeight normalizedValue
                let tokenDocuments =
                    match spec.TokenWeight with
                    | None -> []
                    | Some tokenWeight ->
                        tokenize spec.Value
                        |> List.filter ((<>) normalizedValue)
                        |> List.map (createDocument sourceType tenantId sourceId title resourceGroupId resourceGroupName spec.KeyType tokenWeight)
                fullDocument :: tokenDocuments)
        |> List.groupBy (fun document -> document.KeyType, document.SearchValue)
        |> List.map (fun (_, documents) -> documents |> List.maxBy _.Weight)

    let fromSnapshot (sourceType : SearchSourceType) (snapshot : SearchProjectionSnapshot) =
        let specs =
            snapshot.Fields
            |> Seq.map (fun field ->
                let keyType, fullWeight, tokenWeight = weights field.Type
                { KeyType = keyType
                  Value = field.Value
                  FullWeight = fullWeight
                  TokenWeight = tokenWeight })
            |> Seq.toList

        { SourceType = sourceType
          TenantId = snapshot.Identity.TenantId
          SourceId = snapshot.Identity.SourceId
          Title = snapshot.Title
          ResourceGroupId = Option.ofObj snapshot.ResourceGroupId
          ResourceGroupName = Option.ofObj snapshot.ResourceGroupName
          Documents =
            toDocuments sourceType snapshot.Identity.TenantId snapshot.Identity.SourceId snapshot.Title
                (Option.ofObj snapshot.ResourceGroupId) (Option.ofObj snapshot.ResourceGroupName) specs }

    let toSourceDocument (documentSet : SearchDocumentSet) =
        let searchKeys =
            documentSet.Documents
            |> List.groupBy _.SearchValue
            |> List.map (fun (value, documents) ->
                { Value = value
                  Weight = documents |> List.maxBy _.Weight |> _.Weight })
            |> List.toArray

        { Id = SearchSourceDocument.createId()
          ResourceTypeId = documentSet.SourceType.ResourceTypeId.Value
          SourceApiName = documentSet.SourceType.ApiName
          SourceSortOrder = documentSet.SourceType.SortOrder
          TenantId = documentSet.TenantId
          SourceId = documentSet.SourceId
          Title = documentSet.Title
          ResourceGroupId = documentSet.ResourceGroupId
          ResourceGroupName = documentSet.ResourceGroupName
          SearchKeys = searchKeys
          OperationalStatus = None }
