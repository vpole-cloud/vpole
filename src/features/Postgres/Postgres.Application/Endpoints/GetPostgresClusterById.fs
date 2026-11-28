// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Features.Core.Multitenancy
open Pole.Features.GitOps.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.ResourceDeployments.Contracts
open Pole.Mediator
open Pole.Validation

type GetPostgresClusterByIdOutcome =
    | PostgresClusterFound of PostgresClusterDto
    | GetPostgresClusterByIdRejected of GetPostgresClusterByIdError

type GetPostgresClusterByIdRequest =
    { [<FromRoute>]
      ClusterId: string
      [<Pole.ASP.Helpers.FromTenantHeader>]
      TenantId: string }

    interface IMediatorRequest<GetPostgresClusterByIdOutcome>

    interface ITenantReadOperation with
        member this.TenantId = this.TenantId

type GetPostgresClusterHandler
    (loadCluster: LoadPostgresClusterById, loadDeployment: LoadResourceDeployment, git: IGitOpsMutations) =
    interface IMediatorOutcomeHandler<GetPostgresClusterByIdRequest, GetPostgresClusterByIdOutcome> with
        member this.HandleAsync(request, cancellationToken) =
            task {
                let! postgresCluster = loadCluster.Invoke(request.TenantId, request.ClusterId, cancellationToken)

                match postgresCluster with
                | None -> return GetPostgresClusterByIdRejected(PostgresClusterNotFound request.ClusterId)
                | Some cluster ->
                    let! placement =
                        loadDeployment.Invoke(
                            LoadResourceDeploymentRequest(
                                cluster.TenantId,
                                PostgresResourceDeployment.ResourceType,
                                cluster.Id
                            ),
                            cancellationToken
                        )

                    if isNull placement then
                        return GetPostgresClusterByIdRejected(PostgresClusterDeploymentUnavailable cluster.Id)
                    else
                        let! status =
                            git.StatusAsync(
                                LoadGitResourceStatusRequest(cluster.TenantId, "PostgresCluster", cluster.Id),
                                cancellationToken
                            )

                        return PostgresClusterDto.create cluster placement status |> PostgresClusterFound
            }

type GetPostgresClusterByIdRequestValidator() as this =
    inherit AbstractValidator<GetPostgresClusterByIdRequest>()
    do this.RuleFor(_.ClusterId).MongoObjectId("PostgreSQL cluster ID") |> ignore

[<AbstractClass; Sealed>]
type GetPostgresClusterById =
    [<Extension>]
    static member MapGetByIdPostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints
            .MapMethod<GetPostgresClusterById>(
                "/{ClusterId}",
                nameof GetPostgresClusterById.Handle,
                EndpointHttpMethod.Get
            )
            .WithName("GetPostgresClusterById")
        |> ignore

        endpoints

    static member Handle
        (
            [<AsParameters>] request: GetPostgresClusterByIdRequest,
            [<FromServices>] mediator: IMediator,
            cancellationToken: System.Threading.CancellationToken
        ) =
        task {
            let! result = mediator.Send(request, cancellationToken)

            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterFound value -> Results.Ok(value)
                    | GetPostgresClusterByIdRejected error ->
                        (PostgresHttpMappings.mapGetByIdError error).ToHttpResult())
        }
