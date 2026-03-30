namespace Pole.Domain.Models.FSharp

open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes

type PostgresClusterSpec =
    { Instances : int
      Storage : int }
    
type PostgresCluster =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id : string
      Name : string
      GroupId : string
      Username : string
      Spec : PostgresClusterSpec }
    
type CreatePostgresClusterOptions =
    { Id : string option
      Instances : int option
      Storage : int option }

module PostgresCluster =
    let emptyOptions =
        { Instances = None
          Storage = None
          Id = None }
        
    let create name groupId username options=
        { Id = defaultArg options.Id (ObjectId.GenerateNewId().ToString())
          Name = name
          Username = username
          Spec = { Instances = defaultArg options.Instances 1
                   Storage = defaultArg options.Storage 10 }
          GroupId = groupId }
