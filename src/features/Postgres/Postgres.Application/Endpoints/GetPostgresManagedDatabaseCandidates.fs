namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open System.Threading
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Application.ManagedDatabases
open Pole.Features.Postgres.Contracts
open Pole.Mediator
open Pole.Validation

type GetPostgresManagedDatabaseCandidatesOutcome = ManagedDatabaseCandidates of PostgresManagedDatabaseCandidate array

type GetPostgresManagedDatabaseCandidatesRequest =
    { [<FromQuery>] DeploymentTargetId: string
      [<FromTenantHeader>] TenantId: string }
    interface IMediatorRequest<GetPostgresManagedDatabaseCandidatesOutcome>
    interface ITenantReadOperation with member this.TenantId = this.TenantId

type GetPostgresManagedDatabaseCandidatesValidator() as this =
    inherit AbstractValidator<GetPostgresManagedDatabaseCandidatesRequest>()
    do this.RuleFor(_.DeploymentTargetId).MongoObjectId("Deployment target ID") |> ignore

type GetPostgresManagedDatabaseCandidatesHandler(read: ReadPostgresManagedDatabaseCandidates) =
    interface IMediatorOutcomeHandler<GetPostgresManagedDatabaseCandidatesRequest, GetPostgresManagedDatabaseCandidatesOutcome> with
        member _.HandleAsync(request, ct) =
            task {
                let! candidates = read.Invoke(request.TenantId, request.DeploymentTargetId, ct)
                return ManagedDatabaseCandidates candidates
            }

[<AbstractClass; Sealed>]
type GetPostgresManagedDatabaseCandidates =
    [<Extension>]
    static member MapGetPostgresManagedDatabaseCandidatesEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<GetPostgresManagedDatabaseCandidates>(
            "/managed-database-candidates", nameof GetPostgresManagedDatabaseCandidates.Handle, EndpointHttpMethod.Get)
            .WithName("GetPostgresManagedDatabaseCandidates") |> ignore
        endpoints

    static member Handle([<AsParameters>] request: GetPostgresManagedDatabaseCandidatesRequest,
                         [<FromServices>] mediator: IMediator, ct: CancellationToken) =
        task {
            let! result = mediator.Send(request, ct)
            return result.ToHttpResult(fun outcome -> match outcome with ManagedDatabaseCandidates candidates -> Results.Ok candidates)
        }
