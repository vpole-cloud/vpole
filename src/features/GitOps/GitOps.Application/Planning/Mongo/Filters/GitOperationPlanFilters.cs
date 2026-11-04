using MongoDB.Driver;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Planning.Mongo.Filters;

internal static class GitOperationPlanFilters
{
    public static FilterDefinition<GitPlannedOperation> Runs(string tenant, IReadOnlyCollection<string> runs) =>
        Builders<GitPlannedOperation>.Filter.Eq(item => item.Stamp.Run.Source.TenantId, tenant) &
        Builders<GitPlannedOperation>.Filter.In(item => item.Stamp.Run.RunId, runs);

    public static FilterDefinition<GitPlannedOperation> Identity(string id) =>
        Builders<GitPlannedOperation>.Filter.Eq(item => item.Id, id);

    public static FilterDefinition<GitPlannedOperation> Run(string tenant, string source, string run) =>
        Builders<GitPlannedOperation>.Filter.Eq(item => item.Stamp.Run.Source.TenantId, tenant) &
        Builders<GitPlannedOperation>.Filter.Eq(item => item.Stamp.Run.Source.SourceId, source) &
        Builders<GitPlannedOperation>.Filter.Eq(item => item.Stamp.Run.RunId, run);
}
