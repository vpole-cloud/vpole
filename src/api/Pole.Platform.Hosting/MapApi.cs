using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.Domains.Application.Common;
using Pole.Features.GitOps.Application;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.PrivateAccess.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Realtime.Application;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.Search.Application.Common;
using Pole.Features.Secrets.Application;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Users.Application.Common;

namespace Pole.Platform.Hosting;

public static class MapApi
{
    public static IEndpointRouteBuilder MapPolePlatformEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapRealtimeEndpoints();
        var group = app.MapGroup("/api/v1").RequireTenantContext().RequireAuthorization();

        group.MapGitOpsEndpoints()
            .MapSecretEndpoints()
            .MapPrivateAccessEndpoints()
            .MapResourceGroupEndpoints()
            .MapContainerRegistryEndpoints()
            .MapDeploymentTargetEndpoints()
            .MapDeploymentTargetAvailabilityEndpoints()
            .MapDeploymentTargetEncryptionEndpoints()
            .MapGitProviderEndpoints()
            .MapSourceBuildEndpoints()
            .MapTenantEndpoints()
            .MapUserEndpoints()
            .MapResourceDeploymentEndpoints()
            .MapQuotaEndpoints()
            .MapStorageProfileEndpoints()
            .MapSearchEndpoints()
            .MapDomainZoneEndpoints();

        group.MapGroup("/platform/compute-profiles")
            .WithoutTenantContext()
            .WithTags("PlatformComputeProfiles")
            .RequireAuthorization(Policy.Admin)
            .MapGetComputeProfilesEndpoint()
            .MapGetComputeProfileEndpoint()
            .MapCreateComputeProfileEndpoint()
            .MapUpdateComputeProfileEndpoint()
            .MapDeleteComputeProfileEndpoint();

        group.MapGroup("/compute-profiles").WithTags("ComputeProfiles").MapGetAvailableComputeProfilesEndpoint();

        return app;
    }
}
