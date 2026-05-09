namespace Pole.Features.Postgres.Common

open Pole.Features.Postgres.Domain

type PostgresClusterSpecDto =
    { Instances : int
      Storage : int
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
              Version = cluster.Spec.Version
              DeploymentMode = cluster.Spec.DeploymentMode.ToString() } }
