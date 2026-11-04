namespace Pole.Features.Postgres.Application.Persistence.Declarative

open System.Threading
open MongoDB.Driver
open Pole.Features.GitOps.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type DeclarativePostgresProvider(
    resources : IMongoCollection<PostgresCluster>,
    settings : PostgresSettings,
    planner : PostgresDeclarationPlanner,
    references : PostgresDeclarationReferences,
    execution : PostgresDeclarationExecution) =

    interface IDeclarativeResourceProvider with
        member _.Kind = PostgresDeclarations.Kind
        member _.MaximumNameLength = 63
        member _.DescribeFailure(failure) = PostgresDeclarations.describe failure
        member _.NormalizeAndValidate(spec) = PostgresDeclarations.normalize settings spec
        member _.Facts(spec) = PostgresDeclarations.facts spec
        member _.Steps(address, spec) = PostgresDeclarations.steps address spec
        member _.ResolveAsync(request, session, ct) =
            task {
                let! current = resources.Find(session, PostgresFilters.byName request.TenantId request.Name)
                                .FirstOrDefaultAsync(ct)
                return if isNull (box current) then null else current.Id
            }
        member _.ApplyAsync(operation, ct) = execution.ApplyAsync(operation, ct)

    interface IDeclarativeResourcePlanningProvider with
        member _.ReadCurrentAsync(request, session, ct) = planner.ReadCurrentAsync(request, session, ct)
        member _.ProjectDesiredAsync(request, session, ct) = planner.ProjectDesiredAsync(request, session, ct)

    interface ITransactionalDeclarativeResourceProvider with
        member _.FenceAsync(request, session, ct) = planner.FenceAsync(request, session, ct)

    interface IDeclarativeReferencePreparation with
        member _.PrepareReferencesAsync(operation, ct) = references.PrepareAsync(operation, ct)
