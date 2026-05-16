namespace Pole.Features.Postgres.Domain

open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes

type PostgresDeploymentMode =
    | Operator = 0
    | StatefulSet = 1

type PostgresClusterSpec =
    { Instances : int
      Storage : int
      KubernetesNamespace : string
      Version : int
      DeploymentMode : PostgresDeploymentMode }
    
type PostgresCluster =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id : string
      TenantId : string
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
        
    let create tenantId name groupId kubernetesNamespace version deploymentMode options=
        { Id = defaultArg options.Id (ObjectId.GenerateNewId().ToString())
          TenantId = tenantId
          Name = name
          Spec = { Instances = options.Instances
                   Storage = options.Storage
                   KubernetesNamespace = kubernetesNamespace
                   Version = version
                   DeploymentMode = deploymentMode }
          GroupId = groupId }
