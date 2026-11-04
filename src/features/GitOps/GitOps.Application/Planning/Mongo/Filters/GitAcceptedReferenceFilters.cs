using MongoDB.Driver;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Planning.Mongo.Filters;

internal static class GitAcceptedReferenceFilters
{
    public static string Key(GitSourceIdentity source, string consumer, string kind, string name) =>
        $"{source.TenantId}/{source.SourceId}/{consumer}/{kind}/{name}";

    public static FilterDefinition<GitAcceptedReference> Identity(string id) =>
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Id, id);

    public static FilterDefinition<GitAcceptedReference> Target(string tenant, string kind, string resource) =>
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Source.TenantId, tenant) &
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Target.Kind, kind) &
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Target.ResourceId, resource);

    public static FilterDefinition<GitAcceptedReference> Consumer(GitSourceIdentity source, string consumer) =>
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Source.TenantId, source.TenantId) &
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Source.SourceId, source.SourceId) &
        Builders<GitAcceptedReference>.Filter.Eq(item => item.ConsumerResourceId, consumer);
}
