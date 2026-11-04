using Pole.Features.GitOps.Domain;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Application.Persistence;

internal static class GitSourceExecutionFilters
{
    public static FilterDefinition<GitSourceExecution> Due(DateTime now) =>
        Builders<GitSourceExecution>.Filter.In(item => item.Status, [new ApplyingSnapshot(), new BlockedSnapshot()]) &
        Builders<GitSourceExecution>.Filter.Ne(item => item.NextAttemptAtUtc, null) &
        Builders<GitSourceExecution>.Filter.Lte(item => item.NextAttemptAtUtc, now);

    public static FilterDefinition<GitSourceExecution> DueForTenant(string? tenant, DateTime now) =>
        tenant is null ? Due(now) : Due(now) & Builders<GitSourceExecution>.Filter.Eq(item => item.TenantId, tenant);

    public static FilterDefinition<GitSourceExecution> SecretWaiters(string tenant, string secretName) =>
        Builders<GitSourceExecution>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<GitSourceExecution>.Filter.In(item => item.Status, [new ApplyingSnapshot(), new BlockedSnapshot()]) &
        Builders<GitSourceExecution>.Filter.AnyEq(item => item.ReferencedSecrets, secretName);

    public static FilterDefinition<GitSourceExecution> Source(string tenant, string source) =>
        Builders<GitSourceExecution>.Filter.Eq(item => item.Id, SourceId(tenant, source));

    public static string SourceId(string tenant, string source) => $"{tenant}/{source}";

    public static FilterDefinition<GitSourceExecution> SourceRun(GitMutationContext context) =>
        Source(
            context.TenantId,
            context.SourceId) & Builders<GitSourceExecution>.Filter.Eq(
            item => item.Fence,
            context.Fence) &
        Builders<GitSourceExecution>.Filter.Eq(item => item.RunId, context.RunId);

    public static FilterDefinition<GitSourceExecution> Run(GitMutationContext context) =>
        SourceRun(context) & Builders<GitSourceExecution>.Filter.Eq(item => item.Attempt, context.Attempt);
}
