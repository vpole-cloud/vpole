namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open FluentValidation
open MassTransit
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open MongoDB.Driver
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.Core.Multitenancy
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Events
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts
open Pole.Mediator
open Pole.ResultType

type CreatePostgresClusterRequestBody =
    { Name : string
      GroupId : string
      Version : int
      Instances : int option
      Storage : int option
      Resources : Resources
      StorageProfileId : string
      Username : string
      Password : string }

type CreatePostgresClusterRequest =
    { [<FromBody>] Body : CreatePostgresClusterRequestBody
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
        interface IMediatorRequest<PostgresClusterDto>
        interface ITenantCreateOperation with
            member this.TenantId = this.TenantId

type CreatePostgresClusterHandler(
    dbContext : IPostgresDbContext,
    resourceGroupLookup : IResourceGroupLookup,
    storageProfileResolver : IStorageProfileResolver,
    secretStore : IPostgresClusterSecretStore,
    settings : PostgresSettings,
    quotaAdmissionExecutor : IQuotaAdmissionExecutor,
    deploymentTargetLookup : IDeploymentTargetLookup) =
    let validate settings request =
        let validationErrors = System.Collections.Generic.Dictionary<string, string[]>()

        if not (PostgresSettings.isSupportedVersion settings request.Body.Version) then
            validationErrors["Body.Version"] <- [| $"Supported PostgreSQL versions are from {settings.SupportedVersions.From} to {settings.SupportedVersions.``To``}." |]

        let requestedInstances = defaultArg request.Body.Instances PostgresCluster.DefaultInstances
        if settings.DefaultDeploymentMode = PostgresDeploymentMode.StatefulSet && requestedInstances <> 1 then
            validationErrors["Body.Instances"] <- [| "StatefulSet PostgreSQL supports exactly one instance." |]

        if System.String.IsNullOrWhiteSpace request.Body.StorageProfileId then
            validationErrors["Body.StorageProfileId"] <- [| "StorageProfileId is required." |]

        validationErrors

    interface IMediatorHandler<CreatePostgresClusterRequest, PostgresClusterDto> with
        member this.HandleAsync(request, cancellationToken) =
            task {
                let! targetAdmission =
                    deploymentTargetLookup.ResolveForNewWorkloadAsync(null, cancellationToken)

                match targetAdmission.TryGetValue() with
                | struct (false, _, targetAdmissionFailure) ->
                    return targetAdmissionFailure |> ResultF.ofFailure
                | struct (true, _, _) ->
                    let validationErrors = validate settings request

                    if validationErrors.Count > 0 then
                        return Failures.ValidationFailure(validationErrors) |> ResultF.ofFailure
                    else
                        let tenantId = request.TenantId
                        let! placementResult =
                            resourceGroupLookup.GetPlacementAsync(
                                tenantId,
                                request.Body.GroupId,
                                cancellationToken)

                        match placementResult.TryGetValue() with
                        | struct (false, _, placementFailure) ->
                            return placementFailure |> ResultF.ofFailure
                        | struct (true, placement, _) ->
                            let requestedStorage = defaultArg request.Body.Storage PostgresCluster.DefaultStorage
                            let! storageProfileResult =
                                storageProfileResolver.ResolveAsync(
                                    StorageProfileResolutionRequest(
                                        request.Body.StorageProfileId,
                                        tenantId,
                                        ResourceType.Postgres,
                                        requestedStorage),
                                    cancellationToken)

                            match storageProfileResult.TryGetValue() with
                            | struct (false, _, storageProfileFailure) ->
                                return storageProfileFailure |> ResultF.ofFailure
                            | struct (true, storageProfile, _) ->
                                let cluster =
                                    PostgresCluster.create
                                        tenantId
                                        request.Body.Name
                                        request.Body.GroupId
                                        placement.KubernetesNamespace
                                        request.Body.Version
                                        settings.DefaultDeploymentMode
                                        storageProfile
                                        { Id = None
                                          Instances = defaultArg request.Body.Instances PostgresCluster.DefaultInstances
                                          Storage = requestedStorage
                                          Resources = request.Body.Resources }

                                return!
                                    quotaAdmissionExecutor.ExecuteAsync<PostgresClusterSpec, PostgresClusterDto>(
                                        tenantId,
                                        PostgresQuotaResourceKey.create cluster.Id,
                                        CreateQuotaMutation<PostgresClusterSpec>(cluster.Spec),
                                        (fun session ct ->
                                            task {
                                                do! dbContext.Postgres.InsertOneAsync(session, cluster, cancellationToken = ct)
                                                do! secretStore.CreateAsync(placement.KubernetesNamespace, cluster.Id, request.Body.Username, request.Body.Password, ct)
                                                let event = PostgresClusterEvents.created cluster
                                                do! dbContext.StoreEventAsync(event, session, ct)
                                                return PostgresCluster.toDto cluster |> ResultF.ofValue
                                            }),
                                        cancellationToken)
            }
            
type CreatePostgresClusterValidator() as this =
    inherit AbstractValidator<CreatePostgresClusterRequest>()
    do
        this.RuleFor(_.Body.Name).NotEmpty().WithMessage("Name is required.") |> ignore
        this.RuleFor(_.Body.GroupId).NotEmpty().WithMessage("GroupId is required.") |> ignore
        this.RuleFor(_.Body.Version).GreaterThan(0).WithMessage("Version is required.") |> ignore
        this.RuleFor(_.Body.Resources)
            .NotNull()
            .SetValidator(PostgresResourcesValidator())
        |> ignore
        this.RuleFor(_.Body.Username).NotEmpty().WithMessage("Username is required.") |> ignore
        this.RuleFor(_.Body.Password).NotEmpty().WithMessage("Password is required.") |> ignore

[<AbstractClass; Sealed>]
type CreatePostgresCluster =
    [<Extension>]
    static member MapCreatePostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<CreatePostgresCluster>("/", nameof CreatePostgresCluster.Handle, EndpointHttpMethod.Post)
            .WithName("CreatePostgresCluster")
        |> ignore
        endpoints

    static member Handle([<AsParameters>] request: CreatePostgresClusterRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }

type ProvisionPostgresClusterOnPostgresClusterCreatedConsumer(
    secretStore : IPostgresClusterSecretStore,
    deploymentManager : IPostgresClusterDeploymentManager) =
    interface IConsumer<PostgresClusterCreatedEvent> with
        member this.Consume(context) =
            task {
                let event = context.Message
                let cluster = PostgresClusterEvents.fromCreated event
                let! credentials = secretStore.GetAsync(cluster.Spec.KubernetesNamespace, cluster.Id, context.CancellationToken)
                match credentials with
                | None -> ()
                | Some credentials ->
                    do! deploymentManager.CreateAsync(cluster, credentials.Username, context.CancellationToken)
            }

