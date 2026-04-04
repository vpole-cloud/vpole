namespace Pole.Features.Search.Projection

open System.Text.RegularExpressions
open Pole.Domain.Models
open Pole.Domain.Models.FSharp
open Pole.Features.Search.Common

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
    let UsernameWeight = 7000
    [<Literal>]
    let UsernameTokenWeight = 3500
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

    let private createDocument sourceType sourceId title resourceGroupId resourceGroupName keyType weight searchValue =
        { Id = SearchDocument.createId()
          SourceType = sourceType
          SourceId = sourceId
          KeyType = keyType
          SearchValue = searchValue
          Weight = weight
          Title = title
          ResourceGroupId = resourceGroupId
          ResourceGroupName = resourceGroupName }

    let private toDocuments sourceType sourceId title resourceGroupId resourceGroupName specs =
        specs
        |> List.collect (fun spec ->
            let normalizedValue = normalize spec.Value

            if System.String.IsNullOrWhiteSpace(normalizedValue) then
                []
            else
                let fullDocument =
                    createDocument sourceType sourceId title resourceGroupId resourceGroupName spec.KeyType spec.FullWeight normalizedValue

                let tokenDocuments =
                    match spec.TokenWeight with
                    | None -> []
                    | Some tokenWeight ->
                        tokenize spec.Value
                        |> List.filter ((<>) normalizedValue)
                        |> List.map (createDocument sourceType sourceId title resourceGroupId resourceGroupName spec.KeyType tokenWeight)

                fullDocument :: tokenDocuments)
        |> List.groupBy (fun doc -> doc.KeyType, doc.SearchValue)
        |> List.map (fun (_, documents) -> documents |> List.maxBy _.Weight)

    let fromResourceGroup (resourceGroup : ResourceGroup) =
        { SourceType = SearchDocumentType.ResourceGroup
          SourceId = resourceGroup.Id
          Documents =
            toDocuments
                SearchDocumentType.ResourceGroup
                resourceGroup.Id
                resourceGroup.Name
                (Some resourceGroup.Id)
                (Some resourceGroup.Name)
                ({ KeyType = SearchKeyType.Name
                   Value = resourceGroup.Name
                   FullWeight = NameWeight
                   TokenWeight = Some NameTokenWeight }
                 :: (resourceGroup.Tags
                     |> Seq.map (fun tag ->
                         { KeyType = SearchKeyType.Tag
                           Value = tag
                           FullWeight = TagWeight
                           TokenWeight = Some TagTokenWeight })
                     |> Seq.toList)) }

    let fromWebService (resourceGroupName : string option) (webService : WebService) =
        let image = webService.Spec.Image.FullImageName

        { SourceType = SearchDocumentType.WebService
          SourceId = webService.Id
          Documents =
            toDocuments
                SearchDocumentType.WebService
                webService.Id
                webService.Name
                (Some webService.ResourceGroupId)
                resourceGroupName
                [ { KeyType = SearchKeyType.SourceId
                    Value = webService.Id
                    FullWeight = IdWeight
                    TokenWeight = None }
                  { KeyType = SearchKeyType.Name
                    Value = webService.Name
                    FullWeight = NameWeight
                    TokenWeight = Some NameTokenWeight }
                  { KeyType = SearchKeyType.Domain
                    Value = webService.DnsRecord.Domain
                    FullWeight = DomainWeight
                    TokenWeight = Some DomainTokenWeight }
                  { KeyType = SearchKeyType.Image
                    Value = image
                    FullWeight = ImageWeight
                    TokenWeight = Some ImageTokenWeight } ] }

    let fromDnsRecord (resourceGroupName : string option) (dnsRecord : DnsRecord) =
        { SourceType = SearchDocumentType.DnsRecord
          SourceId = dnsRecord.Id
          Documents =
            toDocuments
                SearchDocumentType.DnsRecord
                dnsRecord.Id
                dnsRecord.Name
                (Some dnsRecord.GroupId)
                resourceGroupName
                [ { KeyType = SearchKeyType.Domain
                    Value = dnsRecord.Name
                    FullWeight = DomainWeight
                    TokenWeight = Some DomainTokenWeight }
                  { KeyType = SearchKeyType.ResourceName
                    Value = dnsRecord.ResourceName
                    FullWeight = ResourceNameWeight
                    TokenWeight = Some ResourceNameTokenWeight } ] }

    let fromRedis (resourceGroupName : string option) (redis : Redis) =
        { SourceType = SearchDocumentType.RedisService
          SourceId = redis.Id
          Documents =
            toDocuments
                SearchDocumentType.RedisService
                redis.Id
                redis.Name
                (Some redis.ResourceGroupId)
                resourceGroupName
                [ { KeyType = SearchKeyType.SourceId
                    Value = redis.Id
                    FullWeight = IdWeight
                    TokenWeight = None }
                  { KeyType = SearchKeyType.Name
                    Value = redis.Name
                    FullWeight = NameWeight
                    TokenWeight = Some NameTokenWeight } ] }

    let fromPostgresCluster (resourceGroupName : string option) (cluster : PostgresCluster) =
        { SourceType = SearchDocumentType.PostgresCluster
          SourceId = cluster.Id
          Documents =
            toDocuments
                SearchDocumentType.PostgresCluster
                cluster.Id
                cluster.Name
                (Some cluster.GroupId)
                resourceGroupName
                [ { KeyType = SearchKeyType.SourceId
                    Value = cluster.Id
                    FullWeight = IdWeight
                    TokenWeight = None }
                  { KeyType = SearchKeyType.Name
                    Value = cluster.Name
                    FullWeight = NameWeight
                    TokenWeight = Some NameTokenWeight }
                  { KeyType = SearchKeyType.Username
                    Value = cluster.Username
                    FullWeight = UsernameWeight
                    TokenWeight = Some UsernameTokenWeight } ] }

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
          SourceId = documentSet.SourceId
          Title = title
          ResourceGroupId = resourceGroupId
          ResourceGroupName = resourceGroupName
          SearchKeys = searchKeys }
