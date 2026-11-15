using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Pole.Features.SeaweedFS.Application.Endpoints;

namespace Pole.Features.SeaweedFS.Application.Common;

public static class SeaweedFSEndpoints
{
    public static IEndpointRouteBuilder MapSeaweedFSEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/seaweedfs").WithTags("SeaweedFS");
        group.MapCreateSeaweedFSServiceEndpoint();
        group.MapUpdateSeaweedFSServiceEndpoint();
        group.MapDeleteSeaweedFSServiceEndpoint();
        group.MapRestoreSeaweedFSServiceEndpoint();
        group.MapCreateSeaweedFSBucketEndpoint();
        group.MapDeleteSeaweedFSBucketEndpoint();
        group.MapRestoreSeaweedFSBucketEndpoint();
        group.MapRotateSeaweedFSBucketCredentialsEndpoint();
        group.MapGetSeaweedFSServiceEndpoint();
        group.MapListSeaweedFSEndpoint();
        group.MapListSeaweedFSBucketsEndpoint();
        group.MapGetSeaweedFSCredentialsEndpoint();
        group.MapGetSeaweedFSBucketCredentialsEndpoint();
        group.MapGetSeaweedFSCapacityEndpoint();
        return app;
    }
}
