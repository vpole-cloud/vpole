namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open MongoDB.Bson
open MongoDB.Driver
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Workloads
open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts
open Pole.Mediator
open Pole.ResultType
open Pole.Validation

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

type CreatePostgresClusterRequest =
    { [<FromBody>] Body : CreatePostgresClusterRequestBody
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
        interface IMediatorRequest<PostgresClusterDto>
        interface ITenantCreateOperation with
            member this.TenantId = this.TenantId

type CreatePostgresClusterHandler(
    dbContext : IPostgresDbContext,
    storageProfileResolver : IStorageProfileResolver,
    credentialService : IPostgresCredentialService,
    settings : PostgresSettings,
    quotaAdmissionExecutor : IQuotaAdmissionExecutor,
    resolveResourceDeploymentTarget : ResolveResourceDeploymentTargetLegacy,
    resolveComputeProfile : ResolveComputeProfile,
    createResourceDeployment : CreateResourceDeploymentLegacy) =

    interface IMediatorHandler<CreatePostgresClusterRequest, PostgresClusterDto> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let targetId =
                        request.Body.Placement
                        |> Option.bind (fun placement ->
                            if System.String.IsNullOrWhiteSpace placement.DeploymentTargetId then
                                None
                            else
                                Some placement.DeploymentTargetId)

                let! targetResult =
                    resolveResourceDeploymentTarget.Invoke(
                        Option.toObj targetId,
                        false,
                        cancellationToken)

                match targetResult.TryGetValue() with
                | struct (false, _, failure) ->
                    return failure |> ResultF.ofFailure
                | struct (true, target, _) ->
                    let! computeProfileResult =
                        resolveComputeProfile.Invoke(
                            ComputeProfileResolutionRequest(
                                Option.toObj request.Body.ComputeProfileId,
                                target.DeploymentTargetId,
                                request.TenantId,
                                PostgresResourceDeployment.ResourceType),
                            cancellationToken)

                    match computeProfileResult.TryGetValue() with
                    | struct (false, _, failure) ->
                        return failure |> ResultF.ofFailure
                    | struct (true, computeProfile, _)
                        when settings.DefaultDeploymentMode = PostgresDeploymentMode.Operator &&
                             not (System.String.IsNullOrWhiteSpace computeProfile.RuntimeClassName) ->
                        let errors =
                            System.Collections.Generic.Dictionary<string, string[]>()
                        errors["Body.ComputeProfileId"] <-
                            [| "CloudNativePG compute profiles cannot specify a runtime class." |]
                        return
                            Failures.SemanticValidation(errors)
                            |> ResultF.ofFailure
                    | struct (true, computeProfile, _) ->
                        let requestedStorage =
                            defaultArg request.Body.Storage PostgresCluster.DefaultStorage
                        let! storageProfileResult =
                            storageProfileResolver.ResolveAsync(
                                StorageProfileResolutionRequest(
                                    request.Body.StorageProfileId,
                                    target.DeploymentTargetId,
                                    request.TenantId,
                                    ResourceType.Postgres,
                                    requestedStorage),
                                cancellationToken)

                        match storageProfileResult.TryGetValue() with
                        | struct (false, _, failure) ->
                            return failure |> ResultF.ofFailure
                        | struct (true, storageProfile, _) ->
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
                                credentialService.CreateAsync(
                                    cluster.TenantId,
                                    cluster.Id,
                                    request.Body.Username,
                                    request.Body.Password,
                                    cancellationToken)

                            match credentialResult.TryGetValue() with
                            | struct (false, _, failure) ->
                                return failure |> ResultF.ofFailure
                            | struct (true, credential, _) ->
                                let deploymentId = ObjectId.GenerateNewId().ToString()
                                let quotaInput =
                                    PostgresQuotaInput.create
                                        computeProfile.Requests.CpuMillis
                                        computeProfile.Requests.MemoryMi
                                        (int64 cluster.Spec.Instances)

                                return!
                                    quotaAdmissionExecutor.ExecuteAsync<PostgresQuotaInput, PostgresClusterDto>(
                                        cluster.TenantId,
                                        PostgresQuotaResourceKey.create cluster.Id,
                                        CreateQuotaMutation<PostgresQuotaInput>(quotaInput),
                                        (fun session ct ->
                                            task {
                                                do!
                                                    dbContext.Postgres.InsertOneAsync(
                                                        session,
                                                        cluster,
                                                        cancellationToken = ct)
                                                do!
                                                    (PostgresCredentials.collection dbContext)
                                                        .InsertOneAsync(
                                                            session,
                                                            credential,
                                                            cancellationToken = ct)

                                                let isolationScopeId =
                                                    request.Body.Placement
                                                    |> Option.bind (fun placement ->
                                                        if System.String.IsNullOrWhiteSpace placement.IsolationScopeId then
                                                            None
                                                        else
                                                            Some placement.IsolationScopeId)

                                                let! deploymentResult =
                                                    createResourceDeployment.Invoke(
                                                        CreateResourceDeploymentRequest(
                                                            deploymentId,
                                                            cluster.TenantId,
                                                            PostgresResourceDeployment.ResourceType,
                                                            cluster.Id,
                                                            cluster.GroupId,
                                                            Option.toObj isolationScopeId,
                                                            target,
                                                            PostgresResourceDeploymentMappings.compute computeProfile,
                                                            PostgresResourceDeploymentMappings.storage storageProfile,
                                                            ResourcePlacementCapability.SingleTarget),
                                                        session,
                                                        ct)

                                                match deploymentResult.TryGetValue() with
                                                | struct (false, _, failure) ->
                                                    return failure |> ResultF.ofFailure
                                                | struct (true, deployment, _) ->
                                                    do!
                                                        dbContext.StoreEventAsync(
                                                            PostgresClusterEvents.created cluster,
                                                            session,
                                                            ct)

                                                    return
                                                        PostgresClusterDto.create
                                                            cluster
                                                            deployment
                                                        |> ResultF.ofValue
                                            }),
                                        cancellationToken)
            }

type CreatePostgresClusterValidator(settings : PostgresSettings) as this =
    inherit AbstractValidator<CreatePostgresClusterRequest>()
    do
        this.RuleFor(_.Body.Name).NotEmpty().WithMessage("Name is required.") |> ignore
        this.RuleFor(_.Body.GroupId).NotEmpty().WithMessage("GroupId is required.") |> ignore
        this.RuleFor(_.Body.Version).GreaterThan(0).WithMessage("Version is required.") |> ignore
        this.RuleFor(_.Body.StorageProfileId)
            .NotEmpty()
            .WithMessage("StorageProfileId is required.")
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
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return result.ToHttpResult(fun value -> Results.Accepted(value = value))
        }
