namespace Pole.Features.Postgres.Endpoints

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
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Common
open Pole.Features.Postgres.Events
open Pole.Features.Postgres.Kubernetes
open Pole.Mediator
open Pole.ResultType

type CreatePostgresClusterRequestBody =
    { Name : string
      GroupId : string
      Version : int
      Instances : int option
      Storage : int option
      Username : string
      Password : string }

type CreatePostgresClusterRequest =
    { [<FromBody>] Body : CreatePostgresClusterRequestBody }
        interface IMediatorRequest<PostgresClusterDto>

type CreatePostgresClusterHandler(
    dbContext : IPostgresDbContext,
    secretStore : IPostgresClusterSecretStore,
    settings : PostgresSettings) =
    let validate settings request =
        let validationErrors = System.Collections.Generic.Dictionary<string, string[]>()

        if not (PostgresSettings.isSupportedVersion settings request.Body.Version) then
            validationErrors["Body.Version"] <- [| $"Supported PostgreSQL versions are from {settings.SupportedVersions.From} to {settings.SupportedVersions.``To``}." |]

        let requestedInstances = defaultArg request.Body.Instances PostgresCluster.emptyOptions.Instances
        if settings.DefaultDeploymentMode = PostgresDeploymentMode.StatefulSet && requestedInstances <> 1 then
            validationErrors["Body.Instances"] <- [| "StatefulSet PostgreSQL supports exactly one instance." |]

        validationErrors

    interface IMediatorHandler<CreatePostgresClusterRequest, PostgresClusterDto> with
        member this.HandleAsync(request, cancellationToken) =
            task {
                let validationErrors = validate settings request

                if validationErrors.Count > 0 then
                    return Result.ValidationFailure(validationErrors) |> ResultF.ofFailure
                else
                    let! resourceGroup =
                        dbContext.ResourceGroups
                            .Find(fun c -> c.Id = request.Body.GroupId)
                            .FirstOrNoneAsync(cancellationToken)

                    match resourceGroup with
                    | None ->
                        return Result.NotFound($"Resource group with ID '{request.Body.GroupId}' not found.")
                               |> ResultF.ofFailure
                    | Some group ->
                        let cluster =
                            PostgresCluster.create
                                request.Body.Name
                                request.Body.GroupId
                                group.KubernetesNamespace
                                request.Body.Version
                                settings.DefaultDeploymentMode
                                { PostgresCluster.emptyOptions with
                                    Instances = defaultArg request.Body.Instances PostgresCluster.emptyOptions.Instances
                                    Storage = defaultArg request.Body.Storage PostgresCluster.emptyOptions.Storage }

                        return!
                            dbContext.RunInTransactionAsync<PostgresClusterDto>(
                                (fun session ct ->
                                    task {
                                        do! dbContext.Postgres.InsertOneAsync(session, cluster, cancellationToken = ct)
                                        do! secretStore.CreateAsync(group.KubernetesNamespace, cluster.Id, request.Body.Username, request.Body.Password, ct)
                                        let event = CreateEvent.create cluster
                                        
                                        do! dbContext.StoreEventAsync(event, session, ct)

                                        let response = PostgresCluster.toDto cluster
                                        
                                        return Result.Success(response) :> Result<PostgresClusterDto>
                                    }
                                ),
                                cancellationToken = cancellationToken
                            )
            }
            
type CreatePostgresClusterValidator() as this =
    inherit AbstractValidator<CreatePostgresClusterRequest>()
    do
        this.RuleFor(_.Body.Name).NotEmpty().WithMessage("Name is required.") |> ignore
        this.RuleFor(_.Body.GroupId).NotEmpty().WithMessage("GroupId is required.") |> ignore
        this.RuleFor(_.Body.Version).GreaterThan(0).WithMessage("Version is required.") |> ignore
        this.RuleFor(_.Body.Username).NotEmpty().WithMessage("Username is required.") |> ignore
        this.RuleFor(_.Body.Password).NotEmpty().WithMessage("Password is required.") |> ignore

[<AbstractClass; Sealed>]
type PostgresClusterCreateEndpoints =
    [<Extension>]
    static member MapCreatePostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<PostgresClusterCreateEndpoints>("/", nameof PostgresClusterCreateEndpoints.Handle, EndpointHttpMethod.Post)
            .WithName("CreatePostgresCluster")
        |> ignore
        endpoints

    static member Handle([<AsParameters>] request: CreatePostgresClusterRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }

type PostgresClusterCreatedConsumer(
    secretStore : IPostgresClusterSecretStore,
    deploymentManager : IPostgresClusterDeploymentManager) =
    interface IConsumer<CreateEvent> with
        member this.Consume(context) =
            task {
                let event = context.Message
                let cluster = CreateEvent.toPostgresCluster event
                let! credentials = secretStore.GetAsync(cluster.Spec.KubernetesNamespace, cluster.Id, context.CancellationToken)
                match credentials with
                | None -> ()
                | Some credentials ->
                    do! deploymentManager.CreateAsync(cluster, credentials.Username, context.CancellationToken)
            }
