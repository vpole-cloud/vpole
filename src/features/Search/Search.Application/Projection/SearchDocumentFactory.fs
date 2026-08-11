namespace Pole.Features.Search.Application.Projection

open System.Text.RegularExpressions
open Pole.Features.Search.Application.Common
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
        if System.String.IsNullOrWhiteSpace(value) then
            ""
        else
            value.Trim().ToLowerInvariant()

    let tokenize (value : string) =
        normalize value
        |> fun normalized -> Regex.Split(normalized, "[^a-z0-9]+")
        |> Array.map _.Trim()
        |> Array.filter (fun token -> token.Length > 1)
        |> Array.distinct
        |> Array.toList

    let private createDocument sourceType tenantId sourceId title resourceGroupId resourceGroupName keyType weight searchValue =
        { Id = SearchDocument.createId()
          SourceType = sourceType
          TenantId = tenantId
          SourceId = sourceId
          KeyType = keyType
          SearchValue = searchValue
          Weight = weight
          Title = title
          ResourceGroupId = resourceGroupId
          ResourceGroupName = resourceGroupName }

    let private toDocuments sourceType tenantId sourceId title resourceGroupId resourceGroupName specs =
        specs
        |> List.collect (fun spec ->
            let normalizedValue = normalize spec.Value

            if System.String.IsNullOrWhiteSpace(normalizedValue) then
                []
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
        |> List.groupBy (fun doc -> doc.KeyType, doc.SearchValue)
        |> List.map (fun (_, documents) -> documents |> List.maxBy _.Weight)

    let fromResourceGroupFields tenantId resourceGroupId name tags =
        { SourceType = SearchDocumentType.ResourceGroup
          TenantId = tenantId
          SourceId = resourceGroupId
          Documents =
            toDocuments
                SearchDocumentType.ResourceGroup
                tenantId
                resourceGroupId
                name
                (Some resourceGroupId)
                (Some name)
                ({ KeyType = SearchKeyType.Name
                   Value = name
                   FullWeight = NameWeight
                   TokenWeight = Some NameTokenWeight }
                 :: (tags
                     |> Seq.map (fun tag ->
                         { KeyType = SearchKeyType.Tag
                           Value = tag
                           FullWeight = TagWeight
                           TokenWeight = Some TagTokenWeight })
                     |> Seq.toList)) }

    let fromWebServiceFields tenantId resourceGroupName webServiceId name resourceGroupId domains image =
        let domainFields =
            domains
            |> Seq.distinct
            |> Seq.map (fun domain ->
                { KeyType = SearchKeyType.Domain
                  Value = domain
                  FullWeight = DomainWeight
                  TokenWeight = Some DomainTokenWeight })
            |> Seq.toList
        let baseFields =
            [ { KeyType = SearchKeyType.SourceId
                Value = webServiceId
                FullWeight = IdWeight
                TokenWeight = None }
              { KeyType = SearchKeyType.Name
                Value = name
                FullWeight = NameWeight
                TokenWeight = Some NameTokenWeight }
              { KeyType = SearchKeyType.Image
                Value = image
                FullWeight = ImageWeight
                TokenWeight = Some ImageTokenWeight } ]

        { SourceType = SearchDocumentType.WebService
          TenantId = tenantId
          SourceId = webServiceId
          Documents =
            toDocuments
                SearchDocumentType.WebService
                tenantId
                webServiceId
                name
                (Some resourceGroupId)
                resourceGroupName
                (baseFields @ domainFields) }

    let fromRedisFields tenantId resourceGroupName redisId name resourceGroupId =
        { SourceType = SearchDocumentType.RedisService
          TenantId = tenantId
          SourceId = redisId
          Documents =
            toDocuments
                SearchDocumentType.RedisService
                tenantId
                redisId
                name
                (Some resourceGroupId)
                resourceGroupName
                [ { KeyType = SearchKeyType.SourceId
                    Value = redisId
                    FullWeight = IdWeight
                    TokenWeight = None }
                  { KeyType = SearchKeyType.Name
                    Value = name
                    FullWeight = NameWeight
                    TokenWeight = Some NameTokenWeight } ] }

    let fromPostgresClusterFields tenantId resourceGroupName clusterId name groupId =
        { SourceType = SearchDocumentType.PostgresCluster
          TenantId = tenantId
          SourceId = clusterId
          Documents =
            toDocuments
                SearchDocumentType.PostgresCluster
                tenantId
                clusterId
                name
                (Some groupId)
                resourceGroupName
                [ { KeyType = SearchKeyType.SourceId
                    Value = clusterId
                    FullWeight = IdWeight
                    TokenWeight = None }
                  { KeyType = SearchKeyType.Name
                    Value = name
                    FullWeight = NameWeight
                    TokenWeight = Some NameTokenWeight } ] }

    let toSourceDocument (documentSet : SearchDocumentSet) =
        let title =
            documentSet.Documents
            |> List.tryHead
            |> Option.map _.Title
            |> Option.defaultValue documentSet.SourceId

        let resourceGroupId =
            documentSet.Documents
            |> List.tryPick _.ResourceGroupId

        let resourceGroupName =
            documentSet.Documents
            |> List.tryPick _.ResourceGroupName

        let searchKeys =
            documentSet.Documents
            |> List.groupBy _.SearchValue
            |> List.map (fun (value, documents) ->
                { Value = value
                  Weight = documents |> List.maxBy _.Weight |> fun document -> document.Weight })
            |> List.toArray

        { Id = SearchSourceDocument.createId()
          SourceType = documentSet.SourceType
          TenantId = documentSet.TenantId
          SourceId = documentSet.SourceId
          Title = title
          ResourceGroupId = resourceGroupId
          ResourceGroupName = resourceGroupName
          SearchKeys = searchKeys
          OperationalStatus = None }
