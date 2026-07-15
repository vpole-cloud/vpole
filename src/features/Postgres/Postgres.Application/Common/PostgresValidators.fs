namespace Pole.Features.Postgres.Application.Common

open FluentValidation
open Pole.Features.Shared.Models

type PostgresResourceValidator() as this =
    inherit AbstractValidator<Resource>()
    do
        this.RuleFor(_.CpuCores).GreaterThan(0) |> ignore
        this.RuleFor(_.MemoryMi).GreaterThan(0) |> ignore

type PostgresResourcesValidator() as this =
    inherit AbstractValidator<Resources>()
    do
        this.RuleFor(_.Requests)
            .NotNull()
            .SetValidator(PostgresResourceValidator())
        |> ignore

        this.RuleFor(_.Limits)
            .NotNull()
            .SetValidator(PostgresResourceValidator())
        |> ignore

        this.RuleFor(_.Requests)
            .Must(fun resources requests ->
                isNull requests
                || isNull resources.Limits
                || (requests.CpuCores <= resources.Limits.CpuCores
                    && requests.MemoryMi <= resources.Limits.MemoryMi))
            .WithMessage("Resource requests must not exceed limits.")
        |> ignore
