using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Endpoints;
using Pole.Features.StorageProfiles.Application.Endpoints.KubernetesInventory;

namespace Pole.Features.StorageProfiles.Application.Common;

public static class StorageProfileEndpoints
{
    public const string PlatformRoute = "/platform/storage";
    public const string TenantRoute = "/storage-profiles";

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapStorageProfileEndpoints()
        {
            app.MapGroup(PlatformRoute)
                .WithoutTenantContext()
                .WithTags("PlatformStorage")
                .MapGetStorageProfilesEndpoint()
                .MapCreateStorageProfileEndpoint()
                .MapUpdateStorageProfileEndpoint()
                .MapGetKubernetesStorageClassesEndpoint()
                .MapGetStorageObservationEndpoint()
                .MapGetKubernetesPersistentVolumesEndpoint()
                .MapGetKubernetesPersistentVolumeClaimsEndpoint();

            app.MapGroup(TenantRoute)
                .WithTags("StorageProfiles")
                .MapGetAvailableStorageProfilesEndpoint();

            return app;
        }
    }
}
