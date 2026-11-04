using Pole.Features.GitOps.Domain;
using MongoDB.Driver;

namespace Pole.Features.GitOps.Application.Persistence;

internal static class GitOperationReceiptFilters
{
    public static FilterDefinition<GitOperationReceipt> ForRun(string tenant, string source, string run, long fence) =>
        Builders<GitOperationReceipt>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.SourceId, source) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.RunId, run) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.Fence, fence);

    public static FilterDefinition<GitOperationReceipt> Identity(string id) => Builders<GitOperationReceipt>.Filter.Eq(
        item => item.Id,
        id);
}
