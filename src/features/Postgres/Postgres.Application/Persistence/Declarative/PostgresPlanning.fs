namespace Pole.Features.Postgres.Application.Persistence.Declarative

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.GitOps.Contracts
open Pole.Features.GitOps.Primitives
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Application.Persistence.Credentials
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Features.StorageProfiles.Contracts

type PostgresCatalogPlanning =
    { Scope : IsolationScopePlanningSnapshot
      Target : DeploymentTargetPlanningSnapshot
      Compute : ComputeProfilePlanningSnapshot
      Storage : StorageProfilePlanningSnapshot }

type PostgresPlanningDependencies =
    { Current : PostgresCluster option
      Catalog : PostgresCatalogPlanning
      Placements : ResourceDeploymentPlanningSnapshot option
      BootstrapSecretId : string
      DeploymentMode : PostgresDeploymentMode
      Bootstrap : BootstrapSecretReady option
      Credentials : ConsumerSecretPlanningSnapshot option
      Adoption : ManagedSecretAdoptionSnapshot option }

type private PostgresPlanningInput =
    { TenantId : string
      ResourceId : string
      Declaration : CompiledDeclaration
      Phase : DeclarationPhase
      Resolve : string -> string -> Task<string>
      Spec : PostgresDeclaration
      Session : IClientSessionHandle
      CancellationToken : CancellationToken }

[<RequireQualifiedAccess>]
module PostgresPlanning =
    let rejected reason : Result<'a, IDeclarationFailure> = Error(PostgresDeclarations.failure reason)

    let bind (next : 'a -> Task<Result<'b, IDeclarationFailure>>) (source : Task<Result<'a, IDeclarationFailure>>) =
        task {
            let! value = source
            match value with
            | Ok accepted -> return! next accepted
            | Error failure -> return Error failure
        }

    let bootstrap (cluster : PostgresCluster) =
        match cluster.Provisioning with
        | :? PreparedPostgresProvisioning as prepared -> Some prepared.Bootstrap
        | :? ConfiguredPostgresProvisioning as configured -> Some configured.Bootstrap
        | _ -> None

    let versions (data : PostgresPlanningDependencies) =
        let catalog = data.Catalog
        [| DeclarationDocumentVersion($"ResourceGroup/{catalog.Scope.ResourceGroupId}",
                catalog.Scope.ResourceGroupRevision)
           DeclarationDocumentVersion($"IsolationScope/{catalog.Scope.IsolationScopeId}", catalog.Scope.ScopeRevision)
           DeclarationDocumentVersion(
               DeclarationVersionKeys.DeploymentTarget(catalog.Target.Target.Id), catalog.Target.Target.Revision)
           DeclarationDocumentVersion(DeclarationVersionKeys.TargetWorkloadConfiguration(catalog.Target.Target.Id),
                catalog.Target.Target.WorkloadConfiguration.Revision)
           DeclarationDocumentVersion(
               DeclarationVersionKeys.ComputeProfilePolicy(catalog.Compute.Compute.ComputeProfileId),
                catalog.Compute.PolicyRevision)
           DeclarationDocumentVersion(DeclarationVersionKeys.StorageProfilePolicy(catalog.Storage.Storage.ProfileId),
                catalog.Storage.PolicyRevision)
           match data.Placements with
           | Some placements ->
               for placement in placements.Items do
                   DeclarationDocumentVersion($"ResourceDeployment/{placement.DeploymentId}", placement.Revision)
           | None -> ()
           match data.Bootstrap with
           | Some ready ->
               DeclarationDocumentVersion($"Secret/{ready.Source.SecretId}", ready.Source.Revision)
               DeclarationDocumentVersion($"Secret/{ready.Source.SecretId}/ReferenceFence", ready.Source.ReferenceFence)
               DeclarationDocumentVersion($"Secret/{ready.Source.SecretId}/ValueVersion", ready.ValueVersion)
           | None -> ()
           match data.Credentials with
           | Some credentials ->
               for secret in credentials.Secrets do
                   DeclarationDocumentVersion($"Secret/{secret.SecretId}", secret.Revision)
                   DeclarationDocumentVersion($"Secret/{secret.SecretId}/ReferenceFence", secret.ReferenceFence)
           | None -> ()
           match data.Adoption with
           | Some credential ->
               DeclarationDocumentVersion($"Secret/{credential.SecretId}", credential.Revision)
               DeclarationDocumentVersion($"Secret/{credential.SecretId}/ReferenceFence", credential.ReferenceFence)
               DeclarationDocumentVersion($"Secret/{credential.SecretId}/ValueVersion", credential.ValueVersion)
           | None -> () |]

type PostgresPlanningReader(
    clusters : IMongoCollection<PostgresCluster>,
    git : IGitOpsMutations,
    loadScope : LoadIsolationScopePlanningSnapshot,
    loadTarget : ResolveDeploymentTargetPlanningSnapshot,
    loadCompute : ResolveComputeProfilePlanningSnapshot,
    loadStorage : ResolveStorageProfilePlanningSnapshot,
    loadPlacements : LoadResourceDeploymentPlanningSnapshot,
    readBootstrap : ReadBootstrapSecret,
    readAdoption : ReadManagedSecretAdoption,
    loadCredentials : LoadConsumerSecretPlanningSnapshot,
    settings : PostgresSettings) =

    let accepted (input : PostgresPlanningInput) kind name =
        input.Resolve kind name

    let readScope (input : PostgresPlanningInput) =
        task {
            let! groupId = accepted input "ResourceGroup" input.Spec.ResourceGroup
            let! scopeId =
                if isNull input.Spec.Placement.IsolationScope then Task.FromResult<string>(null)
                else accepted input "IsolationScope" input.Spec.Placement.IsolationScope

            if isNull groupId then
                return PostgresPlanning.rejected PostgresDeclarationError.ResourceGroupUnavailable
            elif not (isNull input.Spec.Placement.IsolationScope) && isNull scopeId then
                return PostgresPlanning.rejected PostgresDeclarationError.IsolationScopeUnavailable
            else
                let! loaded = loadScope.Invoke(
                    IsolationScopePlanningRequest(input.TenantId, groupId, scopeId),
                    input.Session, input.CancellationToken)
                match loaded.Value with
                | :? IsolationScopePlanningSnapshot as scope -> return Ok scope
                | :? IsolationScopePlanningUnavailable ->
                    return PostgresPlanning.rejected PostgresDeclarationError.IsolationScopeUnavailable
                | _ -> return invalidOp "Unsupported isolation scope planning outcome."
        }

    let readTarget (input : PostgresPlanningInput) (scope : IsolationScopePlanningSnapshot) =
        task {
            let! targetId = accepted input "DeploymentTarget" input.Spec.Placement.DeploymentTarget
            if isNull targetId then
                return PostgresPlanning.rejected PostgresDeclarationError.DeploymentTargetUnavailable
            else
                let! loaded = loadTarget.Invoke(
                    DeploymentTargetAdmissionRequest(input.TenantId,
                        PostgresResourceDeployment.ResourceType, scope.IsolationScopeId, targetId,
                        DeploymentTargetRequiredCapabilities.None), input.Session, input.CancellationToken)
                match loaded.Value with
                | :? DeploymentTargetPlanningSnapshot as target -> return Ok(scope, target)
                | :? DeploymentTargetPlanningUnavailable ->
                    return PostgresPlanning.rejected PostgresDeclarationError.DeploymentTargetUnavailable
                | _ -> return invalidOp "Unsupported deployment target planning outcome."
        }

    let readCompute (input : PostgresPlanningInput) (scope, target : DeploymentTargetPlanningSnapshot) =
        task {
            let! id = accepted input "ComputeProfile" input.Spec.Placement.ComputeProfile.Name
            if isNull id then
                return PostgresPlanning.rejected PostgresDeclarationError.ComputeProfileUnavailable
            else
                let! loaded = loadCompute.Invoke(
                    ComputeProfilePlanningRequest(input.TenantId,
                        PostgresResourceDeployment.ResourceType, id, target.Target.Id,
                        input.Spec.Placement.ComputeProfile.Revision), input.Session, input.CancellationToken)
                match loaded.Value with
                | :? ComputeProfilePlanningSnapshot as compute -> return Ok(scope, target, compute)
                | :? ComputeProfilePlanningUnavailable ->
                    return PostgresPlanning.rejected PostgresDeclarationError.ComputeProfileUnavailable
                | _ -> return invalidOp "Unsupported compute profile planning outcome."
        }

    let readStorage (input : PostgresPlanningInput) (scope, target : DeploymentTargetPlanningSnapshot, compute) =
        task {
            let! id = accepted input "StorageProfile" input.Spec.Storage.Profile.Name
            if isNull id then
                return PostgresPlanning.rejected PostgresDeclarationError.StorageProfileUnavailable
            else
                let! loaded = loadStorage.Invoke(
                    StorageProfilePlanningRequest(input.TenantId,
                        PostgresResourceDeployment.ResourceType, id, target.Target.Id,
                        input.Spec.Storage.SizeGb, input.Spec.Storage.Profile.Revision),
                    input.Session, input.CancellationToken)
                match loaded.Value with
                | :? StorageProfilePlanningSnapshot as storage ->
                    return Ok { Scope = scope; Target = target; Compute = compute; Storage = storage }
                | :? StorageProfilePlanningUnavailable ->
                    return PostgresPlanning.rejected PostgresDeclarationError.StorageProfileUnavailable
                | _ -> return invalidOp "Unsupported storage profile planning outcome."
        }

    let readCurrent (input : PostgresPlanningInput) (catalog : PostgresCatalogPlanning) =
        task {
            let! found =
                clusters
                    .Find(input.Session,
                        PostgresFilters.byId input.TenantId input.ResourceId)
                    .FirstOrDefaultAsync(input.CancellationToken)
            let current = if isNull (box found) then None else Some found
            let! sourceId = accepted input "Secret" input.Spec.BootstrapPassword.Secret
            let! placement =
                match current with
                | None -> Task.FromResult<ResourceDeploymentPlanningSnapshot option>(None)
                | Some resource -> task {
                    let! loaded = loadPlacements.Invoke(
                        ResourceDeploymentPlanningRequest(resource.TenantId,
                            PostgresResourceDeployment.ResourceType, resource.Id),
                        input.Session, input.CancellationToken)
                    match loaded.Value with
                    | :? ResourceDeploymentPlanningSnapshot as value -> return Some value
                    | :? ResourceDeploymentPlanningUnavailable -> return None
                    | _ -> return invalidOp "Unsupported PostgreSQL placement planning outcome."
                }

            match current, placement with
            | Some resource, _ when (resource.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                return PostgresPlanning.rejected PostgresDeclarationError.ResourceDeleting
            | Some resource, _ when resource.Name <> input.Declaration.Address.Name ->
                return Error(DeclarationIdentityChanged() :> IDeclarationFailure)
            | Some _, None -> return PostgresPlanning.rejected PostgresDeclarationError.PlacementUnavailable
            | Some resource, Some placed when placed.Items.Count <> 1
                || placed.Items[0].ResourceGroupId <> resource.GroupId
                || placed.Items[0].State = ResourceDeploymentPlanningState.Deleting
                || isNull placed.Items[0].Storage ->
                return PostgresPlanning.rejected PostgresDeclarationError.PlacementUnavailable
            | None, _ when input.Phase <> DeclarationPhase.Prepare ->
                return PostgresPlanning.rejected PostgresDeclarationError.PlacementUnavailable
            | _ when isNull sourceId ->
                return PostgresPlanning.rejected PostgresDeclarationError.BootstrapSecretUnavailable
            | _ ->
                return Ok
                    { Current = current
                      Catalog = catalog
                      Placements = placement
                      BootstrapSecretId = sourceId
                      DeploymentMode = current |> Option.map _.Spec.DeploymentMode
                          |> Option.defaultValue settings.DefaultDeploymentMode
                      Bootstrap = None
                      Credentials = None
                      Adoption = None }
        }

    let checkIdentity (input : PostgresPlanningInput) (data : PostgresPlanningDependencies) =
        task {
            match data.Current, data.Placements with
            | Some current, Some placements ->
                let placement = placements.Items[0]
                let bootstrapChanged =
                    match PostgresPlanning.bootstrap current with
                    | None -> false
                    | Some bootstrap ->
                        bootstrap.SourceSecretId <> data.BootstrapSecretId
                        || bootstrap.SourceKey <> input.Spec.BootstrapPassword.Key
                        || bootstrap.Username <> input.Spec.Username
                let changed =
                    current.GroupId <> data.Catalog.Scope.ResourceGroupId
                    || placement.IsolationScopeId <> data.Catalog.Scope.IsolationScopeId
                    || placement.DeploymentTargetId <> data.Catalog.Target.Target.Id
                    || current.Spec.Version <> input.Spec.Version
                    || current.Spec.StorageProfileId <> data.Catalog.Storage.Storage.ProfileId
                    || placement.Storage <> PostgresResourceDeploymentMappings.storage data.Catalog.Storage.Storage
                    || bootstrapChanged

                if changed then
                    return Error(DeclarationReplacementRequired() :> IDeclarationFailure)
                else
                    return Ok data
            | _ -> return Ok data
        }

    let checkCapacity (input : PostgresPlanningInput) (data : PostgresPlanningDependencies) =
        task {
            if data.DeploymentMode = PostgresDeploymentMode.Operator
               && not (String.IsNullOrWhiteSpace data.Catalog.Compute.Compute.RuntimeClassName) then
                return PostgresPlanning.rejected PostgresDeclarationError.ComputeRuntimeClassUnsupported
            elif data.DeploymentMode = PostgresDeploymentMode.StatefulSet
                 && PostgresDeclarations.instances input.Spec <> 1 then
                return PostgresPlanning.rejected PostgresDeclarationError.StatefulSetInstancesUnsupported
            else
                match data.Current with
                | Some current when not current.IsPrepared
                    && data.DeploymentMode = PostgresDeploymentMode.StatefulSet
                    && input.Spec.Storage.SizeGb <> current.Spec.Storage ->
                    return PostgresPlanning.rejected PostgresDeclarationError.StatefulSetStorageResizeUnsupported
                | Some current when not current.IsPrepared && input.Spec.Storage.SizeGb < current.Spec.Storage ->
                    return PostgresPlanning.rejected PostgresDeclarationError.StorageShrinkUnsupported
                | _ -> return Ok data
        }

    let readManagedCredential
        (input : PostgresPlanningInput)
        (data : PostgresPlanningDependencies)
        (current : PostgresCluster)
        (placements : ResourceDeploymentPlanningSnapshot) =
        task {
            let bootstrap = PostgresPlanning.bootstrap current |> Option.get
            let references = [| ConsumerSecretKeyReference(bootstrap.CredentialId, PostgresManagedSecret.usernameKey)
                                ConsumerSecretKeyReference(bootstrap.CredentialId, PostgresManagedSecret.passwordKey) |]
            let! loaded = loadCredentials.Invoke(
                ConsumerSecretPlanningRequest(current.TenantId, current.GroupId, data.Catalog.Scope.IsolationScopeId,
                    PostgresResourceDeployment.ResourceType, current.Id, references, references,
                    [| placements.Items[0].DeploymentId |]), input.Session, input.CancellationToken)
            match loaded.Value with
            | :? ConsumerSecretPlanningSnapshot as credentials -> return Ok { data with Credentials = Some credentials }
            | :? ConsumerSecretReferenceUnavailable ->
                return PostgresPlanning.rejected PostgresDeclarationError.BootstrapCredentialUnavailable
            | _ -> return invalidOp "Unsupported PostgreSQL managed credential planning outcome."
        }

    let readCredential (input : PostgresPlanningInput) (data : PostgresPlanningDependencies) =
        task {
            match input.Phase, data.Current, data.Placements with
            | _, Some current, Some _ when (current.Provisioning :? DirectPostgresProvisioning) ->
                let! loaded = readAdoption.Invoke(
                    ReadManagedSecretAdoptionRequest(current.TenantId, PostgresManagedSecret.owner current.Id,
                        current.GroupId, data.Catalog.Scope.IsolationScopeId,
                        ManagedSecretIdentityExpectation(PostgresManagedSecret.usernameKey, input.Spec.Username)),
                    input.Session, input.CancellationToken)
                match loaded.Value with
                | :? ManagedSecretAdoptionSnapshot as credential ->
                    return Ok { data with Adoption = Some credential }
                | :? ManagedSecretIdentityMismatch ->
                    return Error(DeclarationReplacementRequired() :> IDeclarationFailure)
                | :? ManagedSecretUnavailable ->
                    return PostgresPlanning.rejected PostgresDeclarationError.BootstrapCredentialUnavailable
                | :? ManagedSecretEncryptionUnavailable ->
                    return PostgresPlanning.rejected PostgresDeclarationError.BootstrapProtectionUnavailable
                | _ -> return invalidOp "Unsupported managed credential adoption outcome."
            | DeclarationPhase.Configure, Some current, Some placements when current.IsPrepared ->
                let request =
                    PostgresBootstrapRequests.create
                        { Cluster = current
                          Placement = BootstrapSecretPlacement(current.GroupId, data.Catalog.Scope.IsolationScopeId,
                              placements.Items[0].DeploymentId)
                          Capacity =
                              { Instances = PostgresDeclarations.instances input.Spec
                                Storage = input.Spec.Storage.SizeGb } }
                let! loaded = readBootstrap.Invoke(request, input.Session, input.CancellationToken)
                match loaded.Value with
                | :? BootstrapSecretReady as ready -> return Ok { data with Bootstrap = Some ready }
                | :? BootstrapSecretInitialized as initialized when initialized.CredentialId = request.CredentialId ->
                    return! readManagedCredential input data current placements
                | :? BootstrapSecretInitialized ->
                    return PostgresPlanning.rejected PostgresDeclarationError.BootstrapCredentialUnavailable
                | :? BootstrapSecretUnavailable as unavailable ->
                    return Error({ Reason = unavailable.Reason } : PostgresSecretDeclarationRejected)
                | _ -> return invalidOp "Unsupported PostgreSQL bootstrap planning outcome."
            | DeclarationPhase.Configure, Some current, Some placements ->
                return! readManagedCredential input data current placements
            | _ -> return Ok data
        }

    let read input =
        readScope input
        |> PostgresPlanning.bind (readTarget input)
        |> PostgresPlanning.bind (readCompute input)
        |> PostgresPlanning.bind (readStorage input)
        |> PostgresPlanning.bind (readCurrent input)
        |> PostgresPlanning.bind (checkIdentity input)
        |> PostgresPlanning.bind (checkCapacity input)
        |> PostgresPlanning.bind (readCredential input)


    member _.ReadAsync(operation : GitOperationInput, session : IClientSessionHandle, ct : CancellationToken) =
        read
            { TenantId = operation.Context.TenantId
              ResourceId = operation.ResourceId
              Declaration = operation.Declaration
              Phase = operation.Phase
              Resolve = fun kind name -> git.ResolveAcceptedAsync(
                  ResolveAcceptedGitReferenceRequest(operation.Context, operation.ResourceId,
                      ResourceAddress(kind, name)), session, ct)
              Spec = DeclarationJson.Read<PostgresDeclaration> operation.Declaration.Spec
              Session = session
              CancellationToken = ct }

    member _.ReadAdoptionAsync(
        request : PreviewLifecycleAdoptionRequest, session : IClientSessionHandle, ct : CancellationToken) =
        read
            { TenantId = request.TenantId
              ResourceId = request.ResourceId
              Declaration = request.Declaration
              Phase = DeclarationPhase.Configure
              Resolve = fun kind name -> Task.FromResult(
                  request.References
                  |> Seq.tryFind (fun reference -> reference.Address = ResourceAddress(kind, name))
                  |> Option.map _.ResourceId
                  |> Option.defaultValue null)
              Spec = DeclarationJson.Read<PostgresDeclaration> request.Declaration.Spec
              Session = session
              CancellationToken = ct }
