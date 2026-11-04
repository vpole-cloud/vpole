using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;

using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

internal sealed class MongoGitOwnership(
    IMongoCollection<GitSourceExecution> sources,
    IMongoCollection<ManagedDeclaration> inventory,
    IServiceProvider services)
{
    public async Task<GitOperationResult?> BeginAsync(GitOperation operation, IClientSessionHandle session, CancellationToken ct)
    {
        GitTransactionGuard.Require(session);
        var context = operation.Context;
        var source = await sources.Find(session, GitSourceExecutionFilters.Run(context)).FirstOrDefaultAsync(ct);
        if (source is null) return new(operation.ResourceId, new StaleGitExecution());
        if (!source.ToDomain().AllowsOperation(context.ToStamp(), operation.ToBinding()))
            return new(operation.ResourceId, new StaleGitExecution());
        var fenced = await sources.UpdateOneAsync(session, GitSourceExecutionFilters.Run(context),
            Builders<GitSourceExecution>.Update.Inc(item => item.MutationFence, 1), cancellationToken: ct);
        if (fenced.ModifiedCount != 1) return new(operation.ResourceId, new StaleGitExecution());
        var current = await inventory.Find(session, ManagedDeclarationFilters.Address(context.TenantId, operation.Declaration.Address)).FirstOrDefaultAsync(ct);
        if (current is not null && !current.ToDomain().CanBeMutatedBy(context.ToStamp(), operation.ResourceId))
            return new(operation.ResourceId, new GitDeclarationOwnershipConflict());
        var desired = current?.ToDomain().ObserveDesired(operation.Declaration.Hash) ?? new DeclarationOwnership(
            context.TenantId, context.SourceId, operation.Declaration.Address.Kind, operation.Declaration.Address.Name,
            operation.ResourceId, operation.Declaration.Hash, null, false);
        if (current is null)
        {
            await inventory.InsertOneAsync(session, new ManagedDeclaration(
                ManagedDeclarationFilters.AddressId(context.TenantId, operation.Declaration.Address), context.TenantId,
                operation.Declaration.Address.Kind, operation.Declaration.Address.Name, context.SourceId,
                context.SourceUrl, operation.ResourceId, desired.DesiredHash, desired.AppliedHash, null, desired.PendingRemoval,
                context.RunId, operation.Phase.ToString(), 0), cancellationToken: ct);
        }
        else
        {
            await inventory.UpdateOneAsync(session, ManagedDeclarationFilters.Address(context.TenantId, operation.Declaration.Address),
                Builders<ManagedDeclaration>.Update.Inc(item => item.MutationFence, 1)
                    .Set(item => item.DesiredHash, desired.DesiredHash).Set(item => item.PendingRemoval, desired.PendingRemoval), cancellationToken: ct);
        }
        return null;
    }
    public async Task<bool> AuthorizeAsync(string tenantId, string kind, string resourceId, IClientSessionHandle session,
        CancellationToken ct, GitMutationContext? context = null)
    {
        GitTransactionGuard.Require(session);
        var owner = await inventory.Find(session, ManagedDeclarationFilters.Resource(tenantId, kind, resourceId)).FirstOrDefaultAsync(ct);
        if (owner is null) return context is null;
        if (context is null || !owner.ToDomain().CanBeMutatedBy(context.ToStamp(), resourceId)) return false;
        var source = await sources.Find(session, GitSourceExecutionFilters.Run(context)).FirstOrDefaultAsync(ct);
        if (source is null || !source.ToDomain().AllowsResourceMutation(context.ToStamp(), owner.ToDomain())) return false;
        var result = await sources.UpdateOneAsync(session, GitSourceExecutionFilters.Run(context),
            Builders<GitSourceExecution>.Update.Inc(item => item.MutationFence, 1), cancellationToken: ct);
        if (result.ModifiedCount != 1) return false;
        await inventory.UpdateOneAsync(session, ManagedDeclarationFilters.Resource(tenantId, kind, resourceId),
            Builders<ManagedDeclaration>.Update.Inc(item => item.MutationFence, 1), cancellationToken: ct);
        return true;
    }

    public async Task<string?> ResolveAsync(string tenantId, ResourceAddress address, IClientSessionHandle session, CancellationToken ct) =>
        services.GetRequiredService<DeclarativeProviderRegistry>().Find(address.Kind) is { } provider ? await provider.ResolveAsync(
            new ResolveDeclaredResourceRequest(
                TenantId: tenantId,
                Name: address.Name),
            session: session,
            ct: ct) : null;
}
