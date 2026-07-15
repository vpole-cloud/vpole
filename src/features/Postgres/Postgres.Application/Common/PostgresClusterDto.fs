namespace Pole.Features.Postgres.Application.Common

open Pole.Features.Postgres.Domain
open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts

type PostgresClusterSpecDto =
    { Instances : int
      Storage : int
      Resources : Resources
      StorageProfile : ResolvedStorageProfileDto
      Version : int
      DeploymentMode : string }
    
type PostgresClusterDto =
    { Id : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpecDto }

module PostgresCluster =
    let toDto (cluster : PostgresCluster) : PostgresClusterDto =
        { Id = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec =
            { Instances = cluster.Spec.Instances
              Storage = cluster.Spec.Storage
              Resources = cluster.Spec.Resources
              StorageProfile = ResolvedStorageProfileExtensions.ToDto cluster.Spec.StorageProfile
              Version = cluster.Spec.Version
              DeploymentMode = cluster.Spec.DeploymentMode.ToString() } }

