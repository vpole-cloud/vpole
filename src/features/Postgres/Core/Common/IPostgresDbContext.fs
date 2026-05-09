namespace Pole.Features.Postgres.Common

open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceGroups.Domain

type IPostgresDbContext =
    inherit IEventDbContext

    abstract Postgres : IMongoCollection<PostgresCluster>
    abstract ResourceGroups : IMongoCollection<ResourceGroup>
