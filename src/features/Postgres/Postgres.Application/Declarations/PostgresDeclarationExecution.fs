namespace Pole.Features.Postgres.Application.Declarations

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Driver
open Pole.Database
open Pole.Features.GitOps.Contracts
open Pole.Features.GitOps.Primitives
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Application.Credentials
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Postgres.Domain
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.Secrets.Contracts

type private PostgresDeclarationExecutionInput =
    { Operation : GitOperation
      Dependencies : PostgresPlanningDependencies }

type PostgresDeclarationExecution(
    resources : IMongoCollection<PostgresCluster>,
    reader : PostgresPlanningReader,
    transactions : MongoRunTransaction,
    git : IGitOpsMutations,
    quotas : IQuotaAdmissionExecutor,
    registrar : IResourceRegistrar,
    createPlacement : CreateResourceDeployment,
    applyPlacements : RequestResourceDeploymentSetApply,
    persistCreated : PersistCreatedPostgresCluster,
    persistUpdated : PersistUpdatedPostgresCluster,
    bootstrap : PostgresBootstrapInitializer) =

    let rejected resourceId reason = GitOperationResult(resourceId, PostgresDeclarations.failure reason)

    let block operation failure ct =
        git.ExecuteAsync(operation, (fun _ _ -> Task.FromResult(GitOperationResult(operation.ResourceId, failure))), ct)

    let prepare (input : PostgresDeclarationExecutionInput) session ct =
        task {
            let operation = input.Operation
            let data = input.Dependencies
            let catalog = data.Catalog
            let spec = DeclarationJson.Read<PostgresDeclaration> operation.Declaration.Spec
            let resource = PostgresCluster.CreatePrepared(
                operation.Context.TenantId, operation.Declaration.Address.Name, catalog.Scope.ResourceGroupId,
                spec.Version, data.DeploymentMode, catalog.Storage.Storage.ProfileId,
                { Id = Some operation.ResourceId
                  Instances = PostgresDeclarations.instances spec
                  Storage = spec.Storage.SizeGb },
                PostgresBootstrapCredential.Create(data.BootstrapSecretId, spec.BootstrapPassword.Key,
                    ObjectId.GenerateNewId().ToString(), spec.Username))
            let! created = persistCreated.Invoke(session, resource, ct)
            match created with
            | PostgresError _ -> return rejected resource.Id PostgresDeclarationError.ExplicitAdoptionRequired
            | PostgresSuccess () ->
                let! registered = registrar.RegisterAsync(
                    RegisterResourceRequest(resource.TenantId, resource.GroupId,
                        ResourceRegistrationDescriptor(PostgresResourceDeployment.ResourceType,
                            resource.Id, resource.Name)), session, ct)
                match TypedResultInterop.resourceRegistration registered with
                | Error _ -> return rejected resource.Id PostgresDeclarationError.ResourceRegistrationUnavailable
                | Ok _ ->
                    let target = catalog.Target.Target
                    let request =
                        PostgresPreparedPlacements.createRequest resource
                            { DeploymentId = ObjectId.GenerateNewId().ToString()
                              IsolationScopeId = catalog.Scope.IsolationScopeId
                              Target =
                                  ResolvedResourceDeploymentTarget(
                                      target.Id, target.WorkloadConfiguration.Revision, null)
                              Compute = PostgresResourceDeploymentMappings.compute catalog.Compute.Compute
                              Storage = PostgresResourceDeploymentMappings.storage catalog.Storage.Storage
                              StoragePolicyRevision = catalog.Storage.PolicyRevision }
                    let! placement = createPlacement.Invoke(request, session, ct)
                    match placement.Value with
                    | :? PlacementSummaryDto -> return GitOperationResult(resource.Id, Changed = true)
                    | :? CreateResourceDeploymentError ->
                        return rejected resource.Id PostgresDeclarationError.PlacementUnavailable
                    | _ -> return invalidOp "Unsupported PostgreSQL placement creation outcome."
        }

    let configureRoot (input : PostgresDeclarationExecutionInput) session ct =
        task {
            let current = input.Dependencies.Current |> Option.get
            let spec = DeclarationJson.Read<PostgresDeclaration> input.Operation.Declaration.Spec
            let capacity = { Instances = PostgresDeclarations.instances spec; Storage = spec.Storage.SizeGb }
            if not current.IsPrepared then
                return Ok(current.UpdateDesiredState(capacity.Instances, capacity.Storage))
            else
                let placement = (input.Dependencies.Placements |> Option.get).Items[0]
                let! initialized = bootstrap.InvokeAsync(
                    { Cluster = current
                      Placement = BootstrapSecretPlacement(current.GroupId,
                          input.Dependencies.Catalog.Scope.IsolationScopeId, placement.DeploymentId)
                      Capacity = capacity }, session, ct)
                match initialized with
                | PostgresBootstrapInitialized configured -> return Ok configured
                | PostgresBootstrapUnavailable reason ->
                    return Error(({ Reason = reason } : PostgresSecretDeclarationRejected) :> IDeclarationFailure)
                | PostgresBootstrapChanged -> return Error(DeclarationPreconditionsChanged() :> IDeclarationFailure)
                | PostgresBootstrapProtectionUnavailable ->
                    return PostgresPlanning.rejected PostgresDeclarationError.BootstrapProtectionUnavailable
                | PostgresBootstrapValuesTooLarge ->
                    return PostgresPlanning.rejected PostgresDeclarationError.BootstrapValuesTooLarge
        }

    let configure (input : PostgresDeclarationExecutionInput) session ct =
        task {
            let operation = input.Operation
            let data = input.Dependencies
            let current = data.Current |> Option.get
            let! configured = configureRoot input session ct
            match configured with
            | Error failure -> return GitOperationResult(current.Id, failure)
            | Ok next ->
                let placement = (data.Placements |> Option.get).Items[0]
                let! applied = applyPlacements.Invoke(
                    ResourceDeploymentSetApplyRequest(current.TenantId, PostgresResourceDeployment.ResourceType,
                        current.Id, next.Revision,
                        [| ResourceDeploymentSetApplyItem(placement.DeploymentId, placement.Revision,
                            ComputeProfileReference(placement.Compute.ComputeProfileId,
                                placement.Compute.ComputeProfileRevision), PreserveIngress(),
                            PostgresResourceDeploymentMappings.compute data.Catalog.Compute.Compute,
                            FixedReplicas = Nullable next.Spec.Instances, PinnedCompute = true) |]), session, ct)
                match applied.Value with
                | :? ResourceDeploymentMutationError as error ->
                    match error.Value with
                    | :? ResourceDeploymentChanged | :? ResourceDeploymentComputeConfigurationChanged ->
                        return GitOperationResult(current.Id, DeclarationPreconditionsChanged())
                    | _ -> return rejected current.Id PostgresDeclarationError.PlacementUnavailable
                | :? MutatedResourceDeployments ->
                    let! persisted = persistUpdated.Invoke(
                        { Current = current; Replacement = next; Mutation = Some operation.Context }, session, ct)
                    match persisted with
                    | PostgresSuccess () -> return GitOperationResult(current.Id, Changed = true)
                    | PostgresError conflict
                        when conflict.Reason = PostgresMutationConflictReason.GitOwnedConfiguration ->
                        return GitOperationResult(current.Id, GitDeclarationOwnershipConflict())
                    | PostgresError _ -> return GitOperationResult(current.Id, DeclarationPreconditionsChanged())
                | _ -> return invalidOp "Unsupported PostgreSQL placement apply outcome."
        }

    let execute (operation : GitOperation) (data : PostgresPlanningDependencies) ct =
        let spec = DeclarationJson.Read<PostgresDeclaration> operation.Declaration.Spec
        let compute = data.Catalog.Compute.Compute
        let nextQuota = PostgresQuotaInput.create compute.Requests.CpuMillis compute.Requests.MemoryMi
                            (int64 (PostgresDeclarations.instances spec))
        let mutation : QuotaMutation<PostgresQuotaInput> =
            match data.Current, data.Placements with
            | None, _ -> CreateQuotaMutation<PostgresQuotaInput>(nextQuota)
            | Some current, Some placements ->
                let previous = placements.Items[0].Compute
                UpdateQuotaMutation<PostgresQuotaInput>(
                    PostgresQuotaInput.create previous.Requests.CpuMillis previous.Requests.MemoryMi
                        (int64 current.Spec.Instances), nextQuota)
            | _ -> invalidOp "An existing PostgreSQL cluster requires its pinned placement."

        quotas.ExecuteOutcomeAsync<PostgresQuotaInput, GitOperationResult>(
            ExecuteQuotaAdmissionRequest<PostgresQuotaInput, GitOperationResult>(
                operation.Context.TenantId, PostgresQuotaResourceKey.create operation.ResourceId, mutation,
                (fun _ -> rejected operation.ResourceId PostgresDeclarationError.QuotaUnavailable),
                (fun transaction token -> task {
                    let! rejected = git.BeginAsync(operation, transaction.Session, token)
                    if not (isNull (box rejected)) then return transaction.Abort rejected
                    else
                        let! authoritative =
                            resources
                                .Find(transaction.Session,
                                    PostgresFilters.byId operation.Context.TenantId operation.ResourceId)
                                .FirstOrDefaultAsync(token)
                        let current = if isNull (box authoritative) then None else Some authoritative
                        if Option.map _.Revision current <> Option.map _.Revision data.Current then
                            return transaction.Abort(GitOperationResult(operation.ResourceId,
                                DeclarationPreconditionsChanged()))
                        else
                            let input = { Operation = operation; Dependencies = { data with Current = current } }
                            let! outcome =
                                match operation.Phase, current with
                                | DeclarationPhase.Prepare, None -> prepare input transaction.Session token
                                | DeclarationPhase.Configure, Some _ -> configure input transaction.Session token
                                | _ -> Task.FromResult(GitOperationResult(operation.ResourceId,
                                            DeclarationPreconditionsChanged()))
                            if not outcome.Applied then return transaction.Abort outcome
                            else
                                let! completed = git.CompleteAsync(
                                    CompleteGitOperationRequest(operation, outcome), transaction.Session, token)
                                return
                                    if completed.Applied then transaction.Commit completed
                                    else transaction.Abort completed
                })), ct)

    member _.ApplyAsync(operation : GitOperation, ct : CancellationToken) =
        task {
            let actionable = operation.Plan.Decision :? CreateDeclarationPlan
                             || operation.Plan.Decision :? UpdateDeclarationPlan
            if not actionable || (operation.Phase <> DeclarationPhase.Prepare
                                  && operation.Phase <> DeclarationPhase.Configure) then
                return! block operation (PostgresDeclarations.failure PostgresDeclarationError.UnexpectedOperation) ct
            else
                let! loaded =
                    transactions.InvokeOutcomeAsync<Result<PostgresPlanningDependencies, IDeclarationFailure>>(
                        (fun transaction token -> task {
                            let! value = reader.ReadAsync(operation.Input, transaction.Session, token)
                            return transaction.Commit value
                        }), ct)
                match loaded with
                | Error failure -> return! block operation failure ct
                | Ok data ->
                    let! result = execute operation data ct
                    match result.Failure with
                    | null -> return result
                    | :? DeclarationPreconditionsChanged | :? StaleGitExecution -> return result
                    | failure -> return! block operation failure ct
        }
