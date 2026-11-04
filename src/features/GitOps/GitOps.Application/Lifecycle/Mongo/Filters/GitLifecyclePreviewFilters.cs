using MongoDB.Driver;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Lifecycle.Mongo.Filters;

internal static class GitLifecyclePreviewFilters
{
    public static FilterDefinition<GitOperationPreview> Source(GitSourceIdentity source) =>
        Builders<GitOperationPreview>.Filter.Eq(item => item.Source.Source.TenantId, source.TenantId) &
        Builders<GitOperationPreview>.Filter.Eq(item => item.Source.Source.SourceId, source.SourceId);

    public static FilterDefinition<GitOperationPreview> Preview(GitSourceIdentity source, string preview) =>
        Source(source) & Builders<GitOperationPreview>.Filter.Eq(item => item.Id, preview);

    public static FilterDefinition<GitOperationPreview> Revision(
        GitSourceIdentity source, string preview, long revision) =>
        Preview(source, preview) & Builders<GitOperationPreview>.Filter.Eq(item => item.Revision, revision);

    public static FilterDefinition<GitOperationPreview> TenantPreviews(string tenant, IEnumerable<string> previews) =>
        Builders<GitOperationPreview>.Filter.Eq(item => item.Source.Source.TenantId, tenant) &
        Builders<GitOperationPreview>.Filter.In(item => item.Id, previews);
}
