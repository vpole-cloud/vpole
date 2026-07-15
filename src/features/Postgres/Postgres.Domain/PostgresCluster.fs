namespace Pole.Features.Postgres.Domain

open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes
open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts

type PostgresDeploymentMode =
    | Operator = 0
    | StatefulSet = 1

type PostgresClusterSpec =
    { Instances : int
      Storage : int
      Resources : Resources
      StorageProfile : ResolvedStorageProfile
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
      Storage : int
      Resources : Resources }

module PostgresCluster =
    [<Literal>]
    let DefaultInstances = 1

    [<Literal>]
    let DefaultStorage = 1
        
    let create tenantId name groupId kubernetesNamespace version deploymentMode storageProfile options =
        { Id = defaultArg options.Id (ObjectId.GenerateNewId().ToString())
          TenantId = tenantId
          Name = name
          Spec = { Instances = options.Instances
                   Storage = options.Storage
                   Resources = options.Resources
                   StorageProfile = storageProfile
                   KubernetesNamespace = kubernetesNamespace
                   Version = version
                   DeploymentMode = deploymentMode }
          GroupId = groupId }
