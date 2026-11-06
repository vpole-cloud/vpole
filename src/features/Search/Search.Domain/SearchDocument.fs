namespace Pole.Features.Search.Domain

open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes
open Pole.Features.Core.Multitenancy

type SearchKeyType =
    | SourceId = 0
    | Name = 1
    | Tag = 2
    | Domain = 3
    | Image = 4
    | ResourceGroupName = 5
    | StorageClassName = 6
    | ResourceName = 7
    | Username = 8
    | GroupId = 9

type SearchDocument =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id : string
      ResourceTypeId : string
      SourceApiName : string
      SourceSortOrder : int
      TenantId : string
      SourceId : string
      KeyType : SearchKeyType
      SearchValue : string
      Weight : int
      Title : string
      ResourceGroupId : string option
      ResourceGroupName : string option }

    interface ITenantOwned with
        member this.TenantId = this.TenantId

type SearchSourceKey =
    { Value : string
      Weight : int }

type SearchOperationalStatus =
    { ResourceRevision : int64
      ObservedGeneration : int64
      Status : string
      FailureCode : string option
      EventOccurredAtUtc : System.DateTime }

type SearchProjectionVersionDocument =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id : string
      ResourceTypeId : string
      TenantId : string
      SourceId : string
      ResourceRevision : int64
      IsDeleted : bool }

    interface ITenantOwned with
        member this.TenantId = this.TenantId

[<BsonIgnoreExtraElements>]
type SearchSourceDocument =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id : string
      ResourceTypeId : string
      SourceApiName : string
      SourceSortOrder : int
      TenantId : string
      SourceId : string
      Title : string
      ResourceGroupId : string option
      ResourceGroupName : string option
      SearchKeys : SearchSourceKey array
      OperationalStatus : SearchOperationalStatus option }

    interface ITenantOwned with
        member this.TenantId = this.TenantId

[<RequireQualifiedAccess>]
module SearchDocument =
    let createId () =
        ObjectId.GenerateNewId().ToString()

[<RequireQualifiedAccess>]
module SearchSourceDocument =
    let createId () =
        ObjectId.GenerateNewId().ToString()

[<RequireQualifiedAccess>]
module SearchProjectionVersionDocument =
    let createId () =
        ObjectId.GenerateNewId().ToString()
