using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Endpoints;

namespace Pole.Features.DeploymentTargets.Application.Common;

public static class DeploymentTargetEndpoints
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapDeploymentTargetEndpoints()
        {
            app.MapGroup("/platform/deployment-targets")
                .WithTags("PlatformDeploymentTargets")
                .RequireAuthorization(Policy.Admin)
                .MapGetDeploymentTargetsEndpoint()
                .MapGetDeploymentTargetEndpoint()
                .MapCreateDeploymentTargetEndpoint()
                .MapUpdateDeploymentTargetEndpoint()
                .MapRotateDeploymentTargetCredentialsEndpoint()
                .MapValidateDeploymentTargetEndpoint()
                .MapDeleteDeploymentTargetEndpoint();

            return app;
        }

        public IEndpointRouteBuilder MapDeploymentTargetEncryptionEndpoints()
        {
            app.MapGroup("/platform/encryption")
                .WithTags("PlatformEncryption")
                .MapRewrapDeploymentTargetCredentialsEndpoint();
            return app;
        }
    }
}
