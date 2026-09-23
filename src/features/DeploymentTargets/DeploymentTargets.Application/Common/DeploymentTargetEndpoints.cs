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
                .WithoutTenantContext()
                .WithTags("PlatformDeploymentTargets")
                .RequireAuthorization(Policy.Admin)
                .MapGetDeploymentTargetsEndpoint()
                .MapGetDeploymentTargetEndpoint()
                .MapCreateDeploymentTargetEndpoint()
                .MapUpdateDeploymentTargetEndpoint()
                .MapUpdateDeploymentTargetNetworkPolicyEndpoint()
                .MapRotateDeploymentTargetCredentialsEndpoint()
                .MapValidateDeploymentTargetEndpoint()
                .MapDeleteDeploymentTargetEndpoint();

            return app;
        }

        public IEndpointRouteBuilder MapDeploymentTargetEncryptionEndpoints()
        {
            app.MapGroup("/platform/encryption")
                .WithoutTenantContext()
                .WithTags("PlatformEncryption")
                .MapRewrapDeploymentTargetCredentialsEndpoint();
            return app;
        }

        /// <summary>
        /// The server composes this group separately from platform administration so tenant
        /// authorization and the active tenant header apply to placement discovery.
        /// </summary>
        public IEndpointRouteBuilder MapDeploymentTargetAvailabilityEndpoints()
        {
            app.MapGroup("/deployment-targets")
                .WithTags("DeploymentTargets")
                .MapGetAvailableDeploymentTargetsEndpoint();

            return app;
        }
    }
}
