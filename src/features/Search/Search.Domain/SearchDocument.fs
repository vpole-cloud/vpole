namespace Pole.Features.Search.Domain

open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes

type SearchDocumentType =
    | ResourceGroup = 0
    | WebService = 1
    | RedisService = 2
    | PostgresCluster = 4

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
      SourceType : SearchDocumentType
      TenantId : string
      SourceId : string
      KeyType : SearchKeyType
      SearchValue : string
      Weight : int
      Title : string
      ResourceGroupId : string option
      ResourceGroupName : string option }

type SearchSourceKey =
    { Value : string
      Weight : int }

type SearchOperationalStatus =
    { DesiredGeneration : int64
      ObservedGeneration : int64
      Status : string
      FailureCode : string option
      EventOccurredAtUtc : System.DateTime }

[<BsonIgnoreExtraElements>]
type SearchSourceDocument =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id : string
      SourceType : SearchDocumentType
      TenantId : string
      SourceId : string
      Title : string
      ResourceGroupId : string option
      ResourceGroupName : string option
      SearchKeys : SearchSourceKey array
      OperationalStatus : SearchOperationalStatus option }

[<RequireQualifiedAccess>]
module SearchDocument =
    let createId () =
        ObjectId.GenerateNewId().ToString()

[<RequireQualifiedAccess>]
module SearchSourceDocument =
    let createId () =
        ObjectId.GenerateNewId().ToString()
