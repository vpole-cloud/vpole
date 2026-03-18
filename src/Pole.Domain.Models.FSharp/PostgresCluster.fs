namespace Pole.Domain.Models.FSharp

open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes

type PostgresDatabase =
    { Id : string
      Name : string }
    
type PostgresClusterSpec =
    { Instances : int
      Storage : int }
    
type PostgresCluster =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpec
      Databases : PostgresDatabase list }
    
type CreatePostgresClusterOptions =
    { Id : string option
      Instances : int option
      Storage : int option
      Databases : PostgresDatabase list option }

module PostgresCluster =
    let emptyOptions =
        { Instances = None
          Storage = None
          Id = None
          Databases = None }
        
    let create name groupId options=
        { Id = defaultArg options.Id (ObjectId.GenerateNewId().ToString())
          Name = name
          Spec = { Instances = defaultArg options.Instances 1
                   Storage = defaultArg options.Storage 10 }
          GroupId = groupId
          Databases = defaultArg options.Databases [] }