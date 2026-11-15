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
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Mediator
open Pole.Validation
open Pole.Workloads

open Pole.Features.Postgres.Application.Creation

type CreatePostgresClusterRequestBody =
    { Name: string
      GroupId: string
      Version: int
      Instances: int option
      Storage: int option
      StorageProfileId: string
      ComputeProfileId: string option
      Password: string
      Placement: PlacementRequest option
      ReplicationMode: PostgresReplicationMode option }

    override _.ToString() =
        "CreatePostgresClusterRequestBody([REDACTED])"

type CreatePostgresClusterOutcome =
    | PostgresClusterCreated of PostgresClusterDto
    | CreatePostgresClusterRejected of CreatePostgresClusterError

type CreatePostgresClusterRequest =
    { [<FromBody>]
      Body: CreatePostgresClusterRequestBody
      [<Pole.ASP.Helpers.FromTenantHeader>]
      TenantId: string }

    interface IMediatorRequest<CreatePostgresClusterOutcome>

    interface ITenantCreateOperation with
        member this.TenantId = this.TenantId

type CreatePostgresClusterHandler(creator: PostgresClusterCreator) =
    interface IMediatorOutcomeHandler<CreatePostgresClusterRequest, CreatePostgresClusterOutcome> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let body = request.Body
                let command =
                    { TenantId = request.TenantId
                      ResourceId = ObjectId.GenerateNewId().ToString()
                      Configuration =
                        { Name = body.Name
                          GroupId = body.GroupId
                          Version = body.Version
                          Instances = body.Instances
                          Storage = body.Storage
                          StorageProfileId = body.StorageProfileId
                          ComputeProfileId = body.ComputeProfileId
                          Placement = body.Placement
                          ReplicationMode = body.ReplicationMode }
                      Authority = PostgresCreationAuthority.NewCluster body.Password }
                let! outcome = creator.CreateAsync(command, cancellationToken)
                return
                    match outcome with
                    | PostgresCreationOutcome.Created created ->
                        PostgresClusterCreated(PostgresClusterDto.create created.Cluster created.Deployment null)
                    | PostgresCreationOutcome.Rejected error -> CreatePostgresClusterRejected error
            }

type CreatePostgresClusterValidator(settings: PostgresSettings) as this =
    inherit AbstractValidator<CreatePostgresClusterRequest>()

    do
        this.RuleFor(_.Body.Name).NotEmpty().WithMessage("Name is required.") |> ignore

        this.RuleFor(_.Body.GroupId).NotEmpty().WithMessage("GroupId is required.").MongoObjectId("Resource group ID")
        |> ignore

        this.RuleFor(_.Body.Version).GreaterThan(0).WithMessage("Version is required.")
        |> ignore

        this
            .RuleFor(_.Body.StorageProfileId)
            .NotEmpty()
            .WithMessage("StorageProfileId is required.")
            .MongoObjectId("Storage profile ID")
        |> ignore

        this.RuleFor(_.Body)
            .Must(fun body ->
                PostgresReplication.valid
                    (defaultArg body.Instances PostgresCluster.DefaultInstances)
                    (defaultArg body.ReplicationMode PostgresReplicationMode.Asynchronous))
            .WithMessage("Synchronous HA PostgreSQL requires at least three instances.")
        |> ignore

        this.RuleFor(_.Body.Password).NotEmpty().WithMessage("Password is required.")
        |> ignore

        this
            .RuleFor(_.Body)
            .Must(fun body ->
                PostgresManagedSecret.totalValueBytes [ PostgresManagedSecret.administrator; body.Password ]
                <= PostgresManagedSecret.maximumValueBytes)
            .WithMessage("The combined UTF-8 credential values must not exceed 64 KiB.")
        |> ignore

        this
            .RuleFor(_.Body.Instances)
            .Must(fun instances -> Option.forall (fun value -> value > 0) instances)
            .WithMessage("Instances must be greater than zero.")
        |> ignore

        this
            .RuleFor(_.Body.Storage)
            .Must(fun storage -> Option.forall (fun value -> value > 0) storage)
            .WithMessage("Storage must be greater than zero.")
        |> ignore

        this
            .RuleFor(fun request -> Option.toObj request.Body.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID")
            .OverridePropertyName("Body.ComputeProfileId")
        |> ignore

        this
            .RuleFor(_.Body.Version)
            .Must(fun version -> PostgresSettings.isSupportedVersion settings version)
            .WithMessage(
                $"Supported PostgreSQL versions are from {settings.SupportedVersions.From} to {settings.SupportedVersions.``To``}."
            )
        |> ignore

        this.When(
            (fun request -> Option.isSome request.Body.Placement),
            (fun () ->
                this
                    .RuleFor(fun request -> request.Body.Placement.Value.IsolationScopeId)
                    .OptionalMongoObjectId("Isolation scope ID")
                |> ignore

                this
                    .RuleFor(fun request -> request.Body.Placement.Value.DeploymentTargetId)
                    .OptionalMongoObjectId("Deployment target ID")
                |> ignore)
        )
        |> ignore

[<AbstractClass; Sealed>]
type CreatePostgresCluster =
    [<Extension>]
    static member MapCreatePostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints
            .MapMethod<CreatePostgresCluster>("/", nameof CreatePostgresCluster.Handle, EndpointHttpMethod.Post)
            .WithName("CreatePostgresCluster")
            .Produces<PostgresClusterDto>(StatusCodes.Status202Accepted)
        |> ignore

        endpoints

    static member Handle
        (
            [<AsParameters>] request: CreatePostgresClusterRequest,
            [<FromServices>] mediator: IMediator,
            cancellationToken: System.Threading.CancellationToken
        ) =
        task {
            let! result = mediator.Send(request, cancellationToken)

            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterCreated value -> Results.Accepted(value = value)
                    | CreatePostgresClusterRejected error -> (PostgresHttpMappings.mapCreateError error).ToHttpResult())
        }
