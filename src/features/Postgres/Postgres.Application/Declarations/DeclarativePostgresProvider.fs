namespace Pole.Features.Postgres.Application.Declarations

open System.Collections.Generic
open Pole.Features.GitOps.Primitives
open MongoDB.Driver
open Pole.Features.GitOps.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Domain

type DeclarativePostgresProvider
    (
        resources: IMongoCollection<PostgresCluster>,
        settings: PostgresSettings,
        planner: PostgresDeclarationPlanner,
        references: PostgresDeclarationReferences,
        execution: PostgresDeclarationExecution,
        adoption: PostgresDeclarationAdoption,
        lifecycle: PostgresDeclarationLifecycle
    ) =

    interface IDeclarativeResourceProvider with
        member _.Kind = PostgresDeclarations.Kind
        member _.MaximumNameLength = 63
        member _.DescribeFailure(failure) = PostgresDeclarations.describe failure

        member _.NormalizeAndValidate(spec) =
            PostgresDeclarations.normalize settings spec

        member _.Facts(spec) = PostgresDeclarations.facts spec
        member _.Steps(address, spec) = PostgresDeclarations.steps address spec

        member _.ResolveAsync(request, session, ct) =
            task {
                let! current =
                    resources
                        .Find(session, PostgresFilters.byName request.TenantId request.Name)
                        .FirstOrDefaultAsync(ct)

                return if isNull (box current) then null else current.Id
            }

        member _.ApplyAsync(operation, ct) = execution.ApplyAsync(operation, ct)

    interface IDeclarativeResourcePlanningProvider with
        member _.ReadCurrentAsync(request, session, ct) =
            planner.ReadCurrentAsync(request, session, ct)

        member _.ProjectDesiredAsync(request, session, ct) =
            planner.ProjectDesiredAsync(request, session, ct)

    interface ITransactionalDeclarativeResourceProvider with
        member _.FenceAsync(request, session, ct) =
            planner.FenceAsync(request, session, ct)

    interface IDeclarativeReferencePreparation with
        member _.PrepareReferencesAsync(operation, ct) = references.PrepareAsync(operation, ct)

    interface IDeclarativeAdoptionPreviewProvider with
        member _.PreviewAdoptionAsync(request, session, ct) =
            adoption.PreviewAsync(request, session, ct)

    interface IDeclarativeResourceAdoptionProvider with
        member _.AdoptAsync(request, session, ct) =
            adoption.AdoptAsync(request, session, ct)

    interface IDeclarativeResourceLifecycleProvider with
        member _.Kind = PostgresDeclarations.Kind

        member _.ReadLifecycleAsync(request, session, ct) =
            lifecycle.ReadAsync(request, session, ct)

        member _.RemoveAsync(request, session, ct) =
            lifecycle.RemoveAsync(request, session, ct)

    interface IDeclarativeRegisteredResourceProvider with
        member _.ResourceType = PostgresResourceDeployment.ResourceType

    interface IDeclarativePreviewReferenceProvider with
        member _.ReadPreviewReferencesAsync(request, session, ct) =
            adoption.ReadReferencesAsync(request, session, ct)

    interface IDeclarativeLifecycleDependencyProvider with
        member _.DependencyOwner = "Postgres"

        member _.ReadDependenciesAsync(request, session, ct) =
            lifecycle.ReadDependenciesAsync(request, session, ct)

        member _.FenceDependenciesAsync(request, session, ct) =
            lifecycle.FenceDependenciesAsync(request, session, ct)

    interface IDeclarativeLifecycleWarningProvider with
        member _.ReadLifecycleWarningsAsync(request, session, ct) =
            task {
                if not session.IsInTransaction then
                    invalidOp "Lifecycle warning reads require an active transaction."

                if
                    request.Action <> GitLifecycleAction.Remove
                    && request.Action <> GitLifecycleAction.Replace
                then
                    return Array.empty<string> :> IReadOnlyList<string>
                else
                    let! current =
                        resources
                            .Find(session, PostgresFilters.byId request.TenantId request.ResourceId)
                            .FirstOrDefaultAsync(ct)

                    if isNull (box current) then
                        return Array.empty<string> :> IReadOnlyList<string>
                    else
                        // StatefulSet deletion explicitly removes its data claim; Operator mode deletes the CNPG CR.
                        let storage =
                            match current.Spec.DeploymentMode with
                            | PostgresDeploymentMode.StatefulSet -> "PostgresStatefulSetDataClaimDeleted"
                            | PostgresDeploymentMode.Operator -> "PostgresOperatorManagesStorageCleanup"
                            | mode -> invalidOp $"Unsupported PostgreSQL deployment mode '{mode}'."

                        return
                            [| storage
                               "PersistentVolumeReclaimPolicyDeterminesDataRetention"
                               if request.Action = GitLifecycleAction.Replace then
                                   "PostgresReplacementRequiresBootstrap" |]
                            :> IReadOnlyList<string>
            }
