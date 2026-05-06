namespace Pole.Features.Postgres.Endpoints

open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Features.Postgres.Common
open Pole.Mediator

type GetSupportedPostgresVersionsResponse =
    { From : int
      ``To`` : int }

type GetSupportedPostgresVersionsRequest() =
    interface IMediatorRequest<GetSupportedPostgresVersionsResponse>

type GetSupportedPostgresVersionsHandler(settings : PostgresSettings) =
    interface IMediatorHandler<GetSupportedPostgresVersionsRequest, GetSupportedPostgresVersionsResponse> with
        member _.HandleAsync(_, _) =
            task {
                return
                    { From = settings.SupportedVersions.From
                      ``To`` = settings.SupportedVersions.``To`` }
                    |> ResultF.ofValue
            }

[<AbstractClass; Sealed>]
type GetSupportedPostgresVersionsEndpoint =
    [<Extension>]
    static member MapGetSupportedPostgresVersionsEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<GetSupportedPostgresVersionsEndpoint>("/supported-versions", nameof GetSupportedPostgresVersionsEndpoint.Handle, EndpointHttpMethod.Get)
            .WithName("GetSupportedPostgresVersions")
            |> ignore
        endpoints

    static member Handle([<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send(GetSupportedPostgresVersionsRequest())
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }
