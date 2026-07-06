namespace Pole.Features.Postgres.Application.Common

open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Domain

type IPostgresDbContext =
    inherit IEventDbContext

    abstract Postgres : IMongoCollection<PostgresCluster>

