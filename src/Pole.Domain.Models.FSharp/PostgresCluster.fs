namespace Pole.Domain.Models.FSharp

open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes

type PostgresClusterSpec =
    { Instances : int
      Storage : int
      KubernetesNamespace : string }
    
type PostgresCluster =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpec }
    
type CreatePostgresClusterOptions =
    { Id : string option
      Instances : int
      Storage : int }

module PostgresCluster =
    let emptyOptions =
        { Instances = 1
          Storage = 1
          Id = None }
        
    let create name groupId kubernetesNamespace options=
        { Id = defaultArg options.Id (ObjectId.GenerateNewId().ToString())
          Name = name
          Spec = { Instances = options.Instances
                   Storage = options.Storage
                   KubernetesNamespace = kubernetesNamespace }
          GroupId = groupId }
