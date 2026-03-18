namespace Pole.Features.Postgres.Common

open Pole.Domain.Models.FSharp

type PostgresDatabaseDto =
    { Id : string
      Name : string }
    
type PostgresClusterSpecDto =
    { Instances : int
      Storage : int }
    
type PostgresClusterDto =
    { Id : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpecDto
      Databases : PostgresDatabaseDto list }

module PostgresCluster =
    let toDto (cluster : PostgresCluster) : PostgresClusterDto =
        { Id = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = { Instances = cluster.Spec.Instances; Storage = cluster.Spec.Storage }
          Databases = cluster.Databases |> List.map (fun db -> { Id = db.Id; Name = db.Name }) }
