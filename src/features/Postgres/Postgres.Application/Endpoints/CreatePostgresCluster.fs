namespace Pole.Features.Postgres.Application.Endpoints

open System
open System.Runtime.CompilerServices
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open MongoDB.Bson
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.Core.Multitenancy
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Application.Persistence.Credentials
open Pole.Features.Postgres.Application.Persistence.Mutations
open Pole.Features.Postgres.Domain
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Mediator
open Pole.Validation
open Pole.Workloads

type CreatePostgresClusterRequestBody =
    { Name : string
      GroupId : string
      Version : int
      Instances : int option
      Storage : int option
      StorageProfileId : string
      ComputeProfileId : string option
      Username : string
      Password : string
      Placement : PlacementRequest option }

    override _.ToString() = "CreatePostgresClusterRequestBody([REDACTED])"

type CreatePostgresClusterOutcome =
    | PostgresClusterCreated of PostgresClusterDto
    | CreatePostgresClusterRejected of CreatePostgresClusterError

type CreatePostgresClusterRequest =
    { [<FromBody>] Body : CreatePostgresClusterRequestBody
      [<Pole.ASP.Helpers.FromTenantHeader>]
      TenantId : string }
        interface IMediatorRequest<CreatePostgresClusterOutcome>
        interface ITenantCreateOperation with
            member this.TenantId = this.TenantId

type private CreatePostgresClusterPlan =
    { Cluster : PostgresCluster
      Credential : PreparedManagedSecret
      DeploymentId : string
      IsolationScopeId : string option
      Target : ResolvedResourceDeploymentTarget
      Compute : ResourceDeploymentComputeConfiguration
      Storage : ResourceDeploymentStorageConfiguration
      QuotaInput : PostgresQuotaInput }

type private CreatePostgresClusterPreparation =
    | Completed of CreatePostgresClusterOutcome
    | ReadyToCreate of CreatePostgresClusterPlan

[<RequireQualifiedAccess>]
module private CreatePostgresClusterWorkflow =
    let private placementIds (request : CreatePostgresClusterRequest) =
        let value selector =
            request.Body.Placement
            |> Option.bind (fun placement ->
                let value = selector placement
                if String.IsNullOrWhiteSpace value then None else Some value)

        value (fun placement -> placement.IsolationScopeId),
        value (fun placement -> placement.DeploymentTargetId)

    let private resolvedTarget (target : DeploymentTargetLookupDto) =
        let ingress =
            target.WorkloadConfiguration.Ingress
            |> Option.ofObj
            |> Option.map (fun value ->
                ResourceDeploymentIngressConfiguration.Create(
                    value.IngressClassName,
                    value.ClusterIssuerName,
                    value.PublicBaseDomain,
                    null))

        ResolvedResourceDeploymentTarget(
            target.Id,
            target.WorkloadConfiguration.Revision,
            ingress |> Option.toObj)

    let prepare
        (admitDeploymentTarget : AdmitDeploymentTargetForNewWorkload)
        (resolveComputeProfile : ResolveComputeProfile)
        (resolveStorageProfile : ResolveStorageProfile)
        (createCredential : CreatePostgresCredential)
        (settings : PostgresSettings)
        (request : CreatePostgresClusterRequest)
        cancellationToken =
        task {
            let isolationScopeId, explicitDeploymentTargetId = placementIds request
            let! targetResult =
                admitDeploymentTarget.Invoke(
                    DeploymentTargetAdmissionRequest(
                        request.TenantId,
                        PostgresResourceDeployment.ResourceType,
                        isolationScopeId |> Option.toObj,
                        explicitDeploymentTargetId |> Option.toObj,
                        DeploymentTargetRequiredCapabilities.None),
                    cancellationToken)

            match TypedResultInterop.targetAdmission targetResult with
            | Error error ->
                return Completed(CreatePostgresClusterRejected(CreateTarget error))
            | Ok target ->
                let! computeProfileResult =
                    resolveComputeProfile.Invoke(
                        ComputeProfileResolutionRequest(
                            request.Body.ComputeProfileId |> Option.toObj,
                            target.Id,
                            request.TenantId,
                            PostgresResourceDeployment.ResourceType),
                        cancellationToken)

                match TypedResultInterop.computeProfile computeProfileResult with
                | Error error ->
                    return Completed(CreatePostgresClusterRejected(CreateCompute error))
                | Ok computeProfile
                    when settings.DefaultDeploymentMode = PostgresDeploymentMode.Operator &&
                         not (String.IsNullOrWhiteSpace computeProfile.RuntimeClassName) ->
                    return
                        Completed(
                            CreatePostgresClusterRejected(
                                CreateState(
                                    CreatePostgresClusterStateError.PostgresClusterComputeRuntimeClassUnsupported target.Id)))
                | Ok computeProfile ->
                    let requestedStorage =
                        defaultArg request.Body.Storage PostgresCluster.DefaultStorage
                    let! storageProfileResult =
                        resolveStorageProfile.Invoke(
                            StorageProfileResolutionRequest(
                                request.Body.StorageProfileId,
                                target.Id,
                                request.TenantId,
                                PostgresResourceDeployment.ResourceType,
                                requestedStorage),
                            cancellationToken)

                    match TypedResultInterop.storageProfile storageProfileResult with
                    | Error error ->
                        return Completed(CreatePostgresClusterRejected(CreateStorage error))
                    | Ok storageProfile ->
                        let cluster =
                            PostgresCluster.Create(
                                request.TenantId,
                                request.Body.Name,
                                request.Body.GroupId,
                                request.Body.Version,
                                settings.DefaultDeploymentMode,
                                request.Body.StorageProfileId,
                                { Id = Some(ObjectId.GenerateNewId().ToString())
                                  Instances =
                                    defaultArg request.Body.Instances PostgresCluster.DefaultInstances
                                  Storage = requestedStorage })
                        let! credentialResult =
                            createCredential.Invoke(
                                cluster.TenantId,
                                cluster.Id,
                                request.Body.Username,
                                request.Body.Password,
                                cancellationToken)

                        match credentialResult with
                        | PostgresError error ->
                            return
                                Completed(
                                    CreatePostgresClusterRejected(CreateCredential error))
                        | PostgresSuccess credential ->
                            let quotaInput =
                                PostgresQuotaInput.create
                                    computeProfile.Requests.CpuMillis
                                    computeProfile.Requests.MemoryMi
                                    (int64 cluster.Spec.Instances)

                            return
                                ReadyToCreate(
                                    { Cluster = cluster
                                      Credential = credential
                                      DeploymentId = ObjectId.GenerateNewId().ToString()
                                      IsolationScopeId = isolationScopeId
                                      Target = resolvedTarget target
                                      Compute =
                                        PostgresResourceDeploymentMappings.compute computeProfile
                                      Storage =
                                        PostgresResourceDeploymentMappings.storage storageProfile
                                      QuotaInput = quotaInput })
        }

    let execute
        (quotaAdmissionExecutor : IQuotaAdmissionExecutor)
        (persistCreatedCluster : PersistCreatedPostgresCluster)
        (resourceRegistrar : IResourceRegistrar)
        (createResourceDeployment : CreateResourceDeployment)
        (createBoundSecret : CreateBoundManagedSecret)
        (plan : CreatePostgresClusterPlan)
        cancellationToken =
        quotaAdmissionExecutor.ExecuteOutcomeAsync<
            PostgresQuotaInput,
            CreatePostgresClusterOutcome>(
            plan.Cluster.TenantId,
            PostgresQuotaResourceKey.create plan.Cluster.Id,
            CreateQuotaMutation<PostgresQuotaInput>(plan.QuotaInput),
            (fun error ->
                CreatePostgresClusterRejected(CreateQuota error)),
            (fun transaction ct ->
                task {
                    do!
                        persistCreatedCluster.Invoke(
                            transaction.Session,
                            plan.Cluster,
                            ct)

                    let! registrationResult =
                        resourceRegistrar.RegisterAsync(
                            plan.Cluster.TenantId,
                            plan.Cluster.GroupId,
                            ResourceRegistrationDescriptor(
                                PostgresResourceDeployment.ResourceType,
                                plan.Cluster.Id,
                                plan.Cluster.Name),
                            transaction.Session,
                            ct)

                    match TypedResultInterop.resourceRegistration registrationResult with
                    | Error error ->
                        return
                            transaction.Abort(
                                CreatePostgresClusterRejected(CreateRegistration error))
                    | Ok _ ->
                        let! deploymentResult =
                            createResourceDeployment.Invoke(
                                CreateResourceDeploymentRequest(
                                    plan.DeploymentId,
                                    plan.Cluster.TenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    plan.Cluster.Id,
                                    plan.Cluster.GroupId,
                                    plan.IsolationScopeId |> Option.toObj,
                                    plan.Target,
                                    plan.Compute,
                                    plan.Storage,
                                    ResourcePlacementCapability.SingleTarget),
                                transaction.Session,
                                ct)

                        match TypedResultInterop.createResourceDeployment deploymentResult with
                        | Error error ->
                            return
                                transaction.Abort(
                                    CreatePostgresClusterRejected(CreateDeployment error))
                        | Ok deployment ->
                            let! secretResult =
                                createBoundSecret.Invoke(
                                    plan.Credential,
                                    plan.Cluster.GroupId,
                                    deployment.IsolationScopeId,
                                    deployment.DeploymentId,
                                    transaction.Session,
                                    ct)
                            match TypedResultInterop.toResult secretResult with
                            | Error error ->
                                return transaction.Abort(
                                    CreatePostgresClusterRejected(
                                        CreateCredential(PostgresCredentialErrors.fromManaged plan.Cluster.Id error)))
                            | Ok _ ->
                                return
                                    transaction.Commit(
                                        PostgresClusterDto.create plan.Cluster deployment
                                        |> PostgresClusterCreated)
                }),
            cancellationToken)

type CreatePostgresClusterHandler(
    persistCreatedCluster : PersistCreatedPostgresCluster,
    resourceRegistrar : IResourceRegistrar,
    resolveStorageProfile : ResolveStorageProfile,
    createCredential : CreatePostgresCredential,
    settings : PostgresSettings,
    quotaAdmissionExecutor : IQuotaAdmissionExecutor,
    admitDeploymentTarget : AdmitDeploymentTargetForNewWorkload,
    resolveComputeProfile : ResolveComputeProfile,
    createResourceDeployment : CreateResourceDeployment,
    createBoundSecret : CreateBoundManagedSecret) =

    interface IMediatorOutcomeHandler<CreatePostgresClusterRequest, CreatePostgresClusterOutcome> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! preparation =
                    CreatePostgresClusterWorkflow.prepare
                        admitDeploymentTarget
                        resolveComputeProfile
                        resolveStorageProfile
                        createCredential
                        settings
                        request
                        cancellationToken

                match preparation with
                | Completed outcome -> return outcome
                | ReadyToCreate plan ->
                    return!
                        CreatePostgresClusterWorkflow.execute
                            quotaAdmissionExecutor
                            persistCreatedCluster
                            resourceRegistrar
                            createResourceDeployment
                            createBoundSecret
                            plan
                            cancellationToken
            }

type CreatePostgresClusterValidator(settings : PostgresSettings) as this =
    inherit AbstractValidator<CreatePostgresClusterRequest>()
    do
        this.RuleFor(_.Body.Name).NotEmpty().WithMessage("Name is required.") |> ignore
        this.RuleFor(_.Body.GroupId)
            .NotEmpty()
            .WithMessage("GroupId is required.")
            .MongoObjectId("Resource group ID")
        |> ignore
        this.RuleFor(_.Body.Version).GreaterThan(0).WithMessage("Version is required.") |> ignore
        this.RuleFor(_.Body.StorageProfileId)
            .NotEmpty()
            .WithMessage("StorageProfileId is required.")
            .MongoObjectId("Storage profile ID")
        |> ignore
        this.RuleFor(_.Body.Username).NotEmpty().WithMessage("Username is required.") |> ignore
        this.RuleFor(_.Body.Password).NotEmpty().WithMessage("Password is required.") |> ignore
        this.RuleFor(_.Body)
            .Must(fun body ->
                PostgresManagedSecret.totalValueBytes [body.Username; body.Password] <=
                    PostgresManagedSecret.maximumValueBytes)
            .WithMessage("The combined UTF-8 credential values must not exceed 64 KiB.")
        |> ignore
        this.RuleFor(_.Body.Instances)
            .Must(fun instances -> Option.forall (fun value -> value > 0) instances)
            .WithMessage("Instances must be greater than zero.")
        |> ignore
        this.RuleFor(_.Body.Storage)
            .Must(fun storage -> Option.forall (fun value -> value > 0) storage)
            .WithMessage("Storage must be greater than zero.")
        |> ignore
        this.RuleFor(fun request -> Option.toObj request.Body.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID")
            .OverridePropertyName("Body.ComputeProfileId")
        |> ignore
        this.RuleFor(_.Body.Version)
            .Must(fun version -> PostgresSettings.isSupportedVersion settings version)
            .WithMessage(
                $"Supported PostgreSQL versions are from {settings.SupportedVersions.From} to {settings.SupportedVersions.``To``}.")
        |> ignore
        this.RuleFor(_.Body.Instances)
            .Must(fun instances ->
                settings.DefaultDeploymentMode <> PostgresDeploymentMode.StatefulSet ||
                defaultArg instances PostgresCluster.DefaultInstances = 1)
            .WithMessage("StatefulSet PostgreSQL supports exactly one instance.")
        |> ignore
        this.When(
            (fun request -> Option.isSome request.Body.Placement),
            (fun () ->
                this.RuleFor(fun request -> request.Body.Placement.Value.IsolationScopeId)
                    .OptionalMongoObjectId("Isolation scope ID")
                |> ignore
                this.RuleFor(fun request -> request.Body.Placement.Value.DeploymentTargetId)
                    .OptionalMongoObjectId("Deployment target ID")
                |> ignore))
        |> ignore

[<AbstractClass; Sealed>]
type CreatePostgresCluster =
    [<Extension>]
    static member MapCreatePostgresClusterEndpoint(endpoints : IEndpointRouteBuilder) =
        endpoints.MapMethod<CreatePostgresCluster>(
            "/",
            nameof CreatePostgresCluster.Handle,
            EndpointHttpMethod.Post)
            .WithName("CreatePostgresCluster")
            .Produces<PostgresClusterDto>(StatusCodes.Status202Accepted)
        |> ignore
        endpoints

    static member Handle(
        [<AsParameters>] request : CreatePostgresClusterRequest,
        [<FromServices>] mediator : IMediator,
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterCreated value -> Results.Accepted(value = value)
                    | CreatePostgresClusterRejected error ->
                        (PostgresHttpMappings.mapCreateError error).ToHttpResult())
        }
