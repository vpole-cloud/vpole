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
open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts
open Pole.Mediator
open Pole.ResultType
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

type CreatePostgresClusterStateError =
    | PostgresClusterComputeRuntimeClassUnsupported of deploymentTargetId : string

type CreatePostgresClusterError =
    | CreateState of CreatePostgresClusterStateError
    | CreateTarget of DeploymentTargetNewWorkloadAdmissionError
    | CreateCompute of ComputeProfileResolutionError
    | CreateStorage of StorageProfileResolutionError
    | CreateCredential of PostgresCredentialError
    | CreateDeployment of CreateResourceDeploymentError
    | CreateQuota of QuotaExecutionError
    interface IUnion with
        member this.Value =
            match this with
            | CreateState error -> box error
            | CreateTarget error -> box error
            | CreateCompute error -> box error
            | CreateStorage error -> box error
            | CreateCredential error -> box error
            | CreateDeployment error -> box error
            | CreateQuota error -> box error

type CreatePostgresClusterOutcome =
    | PostgresClusterCreated of PostgresClusterDto
    | CreatePostgresClusterRejected of CreatePostgresClusterError

type CreatePostgresClusterRequest =
    { [<FromBody>] Body : CreatePostgresClusterRequestBody
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
        interface IMediatorRequest<CreatePostgresClusterOutcome>
        interface ITenantCreateOperation with
            member this.TenantId = this.TenantId

type CreatePostgresClusterHandler(
    persistCreatedCluster : PersistCreatedPostgresCluster,
    resolveStorageProfile : ResolveStorageProfile,
    createCredential : CreatePostgresCredential,
    settings : PostgresSettings,
    quotaAdmissionExecutor : IQuotaAdmissionExecutor,
    admitDeploymentTarget : AdmitDeploymentTargetForNewWorkload,
    resolveComputeProfile : ResolveComputeProfile,
    createResourceDeployment : CreateResourceDeployment) =

    interface IMediatorOutcomeHandler<CreatePostgresClusterRequest, CreatePostgresClusterOutcome> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let isolationScopeId =
                    request.Body.Placement
                    |> Option.bind (fun placement ->
                        if String.IsNullOrWhiteSpace placement.IsolationScopeId then None
                        else Some placement.IsolationScopeId)
                let explicitDeploymentTargetId =
                    request.Body.Placement
                    |> Option.bind (fun placement ->
                        if String.IsNullOrWhiteSpace placement.DeploymentTargetId then None
                        else Some placement.DeploymentTargetId)

                let! targetResult =
                    admitDeploymentTarget.Invoke(
                        DeploymentTargetAdmissionRequest(
                            request.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            Option.toObj isolationScopeId,
                            Option.toObj explicitDeploymentTargetId,
                            DeploymentTargetRequiredCapabilities.None),
                        cancellationToken)
                let mutable target = Unchecked.defaultof<DeploymentTargetLookupDto>
                let mutable targetError =
                    Unchecked.defaultof<DeploymentTargetNewWorkloadAdmissionError>
                if not (TypedResultExtensions.TryGetValue(targetResult, &target, &targetError)) then
                    return CreatePostgresClusterRejected(CreateTarget targetError)
                else
                    let! computeProfileResult =
                        resolveComputeProfile.Invoke(
                            ComputeProfileResolutionRequest(
                                Option.toObj request.Body.ComputeProfileId,
                                target.Id,
                                request.TenantId,
                                PostgresResourceDeployment.ResourceType),
                            cancellationToken)
                    let mutable computeProfile = Unchecked.defaultof<ResolvedComputeProfile>
                    let mutable computeProfileError =
                        Unchecked.defaultof<ComputeProfileResolutionError>
                    if not (TypedResultExtensions.TryGetValue(
                        computeProfileResult,
                        &computeProfile,
                        &computeProfileError)) then
                        return CreatePostgresClusterRejected(CreateCompute computeProfileError)
                    elif settings.DefaultDeploymentMode = PostgresDeploymentMode.Operator &&
                         not (String.IsNullOrWhiteSpace computeProfile.RuntimeClassName) then
                        return
                            CreatePostgresClusterRejected(
                                CreateState(
                                    PostgresClusterComputeRuntimeClassUnsupported target.Id))
                    else
                        let requestedStorage =
                            defaultArg request.Body.Storage PostgresCluster.DefaultStorage
                        let! storageProfileResult =
                            resolveStorageProfile.Invoke(
                                StorageProfileResolutionRequest(
                                    request.Body.StorageProfileId,
                                    target.Id,
                                    request.TenantId,
                                    ResourceType.Postgres,
                                    requestedStorage),
                                cancellationToken)
                        let mutable storageProfile = Unchecked.defaultof<ResolvedStorageProfile>
                        let mutable storageProfileError =
                            Unchecked.defaultof<StorageProfileResolutionError>
                        if not (TypedResultExtensions.TryGetValue(
                            storageProfileResult,
                            &storageProfile,
                            &storageProfileError)) then
                            return CreatePostgresClusterRejected(CreateStorage storageProfileError)
                        else
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
                                return CreatePostgresClusterRejected(CreateCredential error)
                            | PostgresSuccess credential ->
                                let deploymentId = ObjectId.GenerateNewId().ToString()
                                let quotaInput =
                                    PostgresQuotaInput.create
                                        computeProfile.Requests.CpuMillis
                                        computeProfile.Requests.MemoryMi
                                        (int64 cluster.Spec.Instances)
                                let targetIngress = target.WorkloadConfiguration.Ingress
                                let resolvedTarget =
                                    ResolvedResourceDeploymentTarget(
                                        target.Id,
                                        target.WorkloadConfiguration.Revision,
                                        if isNull targetIngress then null
                                        else
                                            ResourceDeploymentIngressConfiguration.Create(
                                                targetIngress.IngressClassName,
                                                targetIngress.ClusterIssuerName,
                                                targetIngress.PublicBaseDomain,
                                                null))
                                return!
                                    quotaAdmissionExecutor.ExecuteOutcomeAsync<
                                        PostgresQuotaInput,
                                        CreatePostgresClusterOutcome>(
                                        cluster.TenantId,
                                        PostgresQuotaResourceKey.create cluster.Id,
                                        CreateQuotaMutation<PostgresQuotaInput>(quotaInput),
                                        Func<QuotaExecutionError, CreatePostgresClusterOutcome>(
                                            fun error ->
                                                CreatePostgresClusterRejected(CreateQuota error)),
                                        Func<
                                            MongoTransactionContext<CreatePostgresClusterOutcome>,
                                            Threading.CancellationToken,
                                            Threading.Tasks.Task<MongoTransactionDecision<CreatePostgresClusterOutcome>>>(
                                            fun transaction ct ->
                                                task {
                                                    do!
                                                        persistCreatedCluster.Invoke(
                                                            transaction.Session,
                                                            cluster,
                                                            credential,
                                                            ct)
                                                    let! deploymentResult =
                                                        createResourceDeployment.Invoke(
                                                            CreateResourceDeploymentRequest(
                                                                deploymentId,
                                                                cluster.TenantId,
                                                                PostgresResourceDeployment.ResourceType,
                                                                cluster.Id,
                                                                cluster.GroupId,
                                                                Option.toObj isolationScopeId,
                                                                resolvedTarget,
                                                                PostgresResourceDeploymentMappings.compute computeProfile,
                                                                PostgresResourceDeploymentMappings.storage storageProfile,
                                                                ResourcePlacementCapability.SingleTarget),
                                                            transaction.Session,
                                                            ct)
                                                    let mutable deployment =
                                                        Unchecked.defaultof<PlacementSummaryDto>
                                                    let mutable deploymentError =
                                                        Unchecked.defaultof<CreateResourceDeploymentError>
                                                    if not (TypedResultExtensions.TryGetValue(
                                                        deploymentResult,
                                                        &deployment,
                                                        &deploymentError)) then
                                                        return
                                                            transaction.Abort(
                                                                CreatePostgresClusterRejected(
                                                                    CreateDeployment deploymentError))
                                                    else
                                                        return
                                                            transaction.Commit(
                                                                PostgresClusterDto.create cluster deployment
                                                                |> PostgresClusterCreated)
                                                }),
                                        cancellationToken)
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
        [<FromServices>] httpProblems : IHttpProblemMapper,
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterCreated value -> Results.Accepted(value = value)
                    | CreatePostgresClusterRejected error ->
                        httpProblems.Map(error).ToHttpResult())
        }
