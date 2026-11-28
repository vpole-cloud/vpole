// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Endpoints

open System.ComponentModel
open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Mvc
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Features.Core.Multitenancy
open Pole.Features.GitOps.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Request
open Pole.Mediator

type GetPostgresClustersOutcome = PostgresClusterPage of PaginatedResponse<PostgresClusterDto>

type GetPostgresClustersRequest() =
    interface IMediatorRequest<GetPostgresClustersOutcome>

    interface ITenantReadOperation with
        member this.TenantId = this.TenantId

    [<Pole.ASP.Helpers.FromTenantHeader>]
    member val TenantId = "" with get, set

    [<FromQuery; DefaultValue(1); Description("The page number to retrieve (1-based).")>]
    member val PageNumber = 1 with get, set

    [<FromQuery; DefaultValue(10); Description("The number of items per page.")>]
    member val PageSize = 10 with get, set

type GetPostgresClustersHandler
    (loadClusters: LoadPostgresClusterPage, loadDeployments: LoadResourceDeployments, git: IGitOpsMutations) =
    interface IMediatorOutcomeHandler<GetPostgresClustersRequest, GetPostgresClustersOutcome> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! page =
                    loadClusters.Invoke(
                        { TenantId = request.TenantId
                          PageNumber = request.PageNumber
                          PageSize = request.PageSize },
                        cancellationToken
                    )

                let clusterIds = page.Items |> Seq.map _.Id |> Seq.toArray

                let! placements =
                    loadDeployments.Invoke(
                        LoadResourceDeploymentsRequest(
                            request.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            clusterIds
                        ),
                        cancellationToken
                    )

                let! statuses =
                    git.StatusesAsync(
                        LoadGitResourceStatusesRequest(request.TenantId, "PostgresCluster", clusterIds),
                        cancellationToken
                    )

                let toDto (cluster: PostgresCluster) =
                    let status =
                        match statuses.TryGetValue(cluster.Id) with
                        | true, value -> value
                        | false, _ -> null

                    match placements.TryGetValue(cluster.Id) with
                    | true, placement -> PostgresClusterDto.create cluster placement status
                    | false, _ -> invalidOp $"PostgreSQL cluster '{cluster.Id}' has no resource deployment."

                return
                    PaginatedResponse<PostgresClusterDto>(
                        Items = (page.Items |> Seq.map toDto),
                        TotalCount = page.TotalCount,
                        PageNumber = request.PageNumber,
                        PageSize = request.PageSize
                    )
                    |> PostgresClusterPage
            }

type GetPostgresClustersRequestValidator() as this =
    inherit AbstractValidator<GetPostgresClustersRequest>()

    do
        this.RuleFor(_.PageNumber).GreaterThan(0) |> ignore
        this.RuleFor(_.PageSize).GreaterThan(0) |> ignore

[<AbstractClass; Sealed>]
type GetPostgresClusters =
    [<Extension>]
    static member MapGetPostgresClustersEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints
            .MapMethod<GetPostgresClusters>("/", nameof GetPostgresClusters.Handle, EndpointHttpMethod.Get)
            .WithName("GetPostgresClusters")
        |> ignore

        endpoints

    static member Handle
        (
            [<AsParameters>] request: GetPostgresClustersRequest,
            [<FromServices>] mediator: IMediator,
            cancellationToken: System.Threading.CancellationToken
        ) =
        task {
            let! result = mediator.Send(request, cancellationToken)

            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterPage value -> Results.Ok(value))
        }
