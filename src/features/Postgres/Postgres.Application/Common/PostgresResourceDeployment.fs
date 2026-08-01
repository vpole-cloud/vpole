namespace Pole.Features.Postgres.Application.Common

open Pole.Features.ResourceDeployments.Contracts

[<AbstractClass; Sealed>]
type PostgresResourceDeployment =
    static member ResourceType = ResourceTypeId.Create("pole/postgresql")
