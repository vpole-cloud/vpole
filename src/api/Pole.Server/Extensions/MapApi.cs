using Pole.Features.DnsRecords.Application.Common;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.RedisServices.Application.Common;
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
            .MapGitProviderEndpoints()
            .MapSourceBuildEndpoints()
            .MapTenantEndpoints()
            .MapUserEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapPostgresClusterEndpoints()
            .MapQuotaEndpoints()
            .MapStorageProfileEndpoints()
            .MapSearchEndpoints()
            .MapDomainNameEndpoints();

        return app;
    }
    
}

