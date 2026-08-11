using Pole.ASP.Helpers;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.Domains.Application.Common;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.Search.Application.Common;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Users.Application.Common;
using Pole.Features.WebServices.Application.Common;

namespace Pole.Server.Extensions;

public static class MapApi
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .RequireAuthorization();

        group.MapResourceGroupEndpoints()
            .MapContainerRegistryEndpoints()
            .MapDeploymentTargetEndpoints()
            .MapDeploymentTargetAvailabilityEndpoints()
            .MapDeploymentTargetEncryptionEndpoints()
            .MapGitProviderEndpoints()
            .MapFunctionAppEndpoints()
            .MapSourceBuildEndpoints()
            .MapTenantEndpoints()
            .MapUserEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapResourceDeploymentEndpoints()
            .MapPostgresClusterEndpoints()
            .MapQuotaEndpoints()
            .MapStorageProfileEndpoints()
            .MapSearchEndpoints()
            .MapDomainZoneEndpoints();

        group.MapGroup("/platform/compute-profiles")
            .WithTags("PlatformComputeProfiles")
            .RequireAuthorization(Policy.Admin)
            .MapGetComputeProfilesEndpoint()
            .MapGetComputeProfileEndpoint()
            .MapCreateComputeProfileEndpoint()
            .MapUpdateComputeProfileEndpoint()
            .MapDeleteComputeProfileEndpoint();

        group.MapGroup("/compute-profiles")
            .WithTags("ComputeProfiles")
            .MapGetAvailableComputeProfilesEndpoint();

        return app;
    }
    
}
