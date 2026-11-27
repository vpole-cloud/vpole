// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Deployments;
using Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;
using Pole.Features.WebServices.Application.Endpoints.DeploymentTokens;
using Pole.Features.WebServices.Application.Endpoints.Releases;

namespace Pole.Features.WebServices.Application.Common;

public static class WebServiceEndpoints
{
    public const string Route = "/web-services";

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapWebServiceEndpoints()
        {
            app.MapGroup(Route)
                .WithTags("WebServices")
                .MapCreateWebServiceEndpoint()
                .MapUpdateWebServiceEndpoint()
                .MapDeleteWebServiceEndpoints()
                .MapGetWebServiceByIdEndpoint()
                .MapGetWebServicesEndpoint()
                .MapGetWebServiceResourceDeploymentsEndpoint()
                .MapGetWebServiceReleasesEndpoint()
                .MapAddWebServiceDeploymentEndpoint()
                .MapUpdateWebServiceDeploymentEndpoint()
                .MapDeleteWebServiceDeploymentEndpoint()
                .MapRetryWebServiceDeploymentEndpoint()
                .MapGetWebServiceDeploymentHostnamesEndpoint()
                .MapAddWebServiceDeploymentHostnameEndpoint()
                .MapDeleteWebServiceDeploymentHostnameEndpoint()
                .MapGetWebServiceDeploymentTokensEndpoint()
                .MapCreateWebServiceDeploymentTokenEndpoint()
                .MapRevokeWebServiceDeploymentTokenEndpoint()
                .MapRedeployWebServiceEndpoint()
                .MapRollbackWebServiceEndpoint()
                .MapUpdateWebServiceImageTagEndpoint();

            return app;
        }
    }
}
