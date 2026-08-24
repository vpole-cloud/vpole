using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Common;

internal static class SourceBuildFilters
{
    internal static FilterDefinition<SourceBuildConfiguration>
        ConfigurationsByTenantAndWebService(string tenantId, string webServiceId) =>
        Builders<SourceBuildConfiguration>.Filter.And(
            Builders<SourceBuildConfiguration>.Filter.ForTenant(tenantId),
            Builders<SourceBuildConfiguration>.Filter.Eq(item => item.WebServiceId, webServiceId));

    internal static FilterDefinition<SourceBuildConfiguration>
        ConfigurationsByTenantAndSourceRepository(string tenantId, string sourceRepositoryId) =>
        Builders<SourceBuildConfiguration>.Filter.And(
            Builders<SourceBuildConfiguration>.Filter.ForTenant(tenantId),
            Builders<SourceBuildConfiguration>.Filter.Eq(item => item.SourceRepositoryId, sourceRepositoryId));
}
