using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

internal sealed class MongoGitOwnership(
    GitRunStore runs,
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitOperationReceipt> receipts,
    IServiceProvider services)
{
    public async Task<GitOperationResult?> BeginAsync(
        GitOperation operation,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        GitTransactionGuard.Require(session);
        var context = operation.Context;
        var run = await runs.TouchAsync(context.ToHandle(), session, ct);
        if (run?.Input is not GitInputAccepted accepted || run.Commit != context.Authority.Commit ||
            !accepted.Snapshot.Resources.Any(item => item.Kind == operation.Declaration.Address.Kind &&
                                                     item.Name == operation.Declaration.Address.Name &&
                                                     item.ResourceId == operation.ResourceId &&
                                                     item.Hash == operation.Declaration.Hash) ||
            !accepted.Snapshot.Operations.Any(item => item.Kind == operation.Declaration.Address.Kind &&
                                                      item.Name == operation.Declaration.Address.Name &&
                                                      item.ResourceId == operation.ResourceId &&
                                                      item.Phase == operation.Phase))
            return new(operation.ResourceId, new StaleGitExecution());

        var receipt = await receipts.Find(session,
            GitOperationReceiptFilters.Identity(
                GitOperationReceipts.ReceiptId(operation))).FirstOrDefaultAsync(ct);
        if (receipt is { Blocker: null } && receipt.ResourceId == operation.ResourceId &&
            receipt.DesiredHash == operation.Declaration.Hash)
            return new(operation.ResourceId);

        var current = await inventory.Find(session,
            ManagedDeclarationFilters.Address(context.TenantId, operation.Declaration.Address)).FirstOrDefaultAsync(ct);
        if (current is not null && !current.ToDomain().CanBeMutatedBy(
                new(context.TenantId, context.SourceId),
                operation.ResourceId))
            return new(operation.ResourceId, new GitDeclarationOwnershipConflict());

        var desired = current?.ToDomain().ObserveDesired(operation.Declaration.Hash) ?? new DeclarationOwnership(
            context.TenantId,
            context.SourceId,
            operation.Declaration.Address.Kind,
            operation.Declaration.Address.Name,
            operation.ResourceId,
            operation.Declaration.Hash,
            null,
            false);
        if (current is null)
            await inventory.InsertOneAsync(session,
                new ManagedDeclaration(
                    ManagedDeclarationFilters.AddressId(context.TenantId, operation.Declaration.Address),
                    context.TenantId,
                    operation.Declaration.Address.Kind,
                    operation.Declaration.Address.Name,
                    context.SourceId,
                    context.SourceUrl,
                    operation.ResourceId,
                    desired.DesiredHash,
                    desired.AppliedHash,
                    null,
                    desired.PendingRemoval,
                    context.RunId,
                    operation.Phase.ToString(),
                    0),
                cancellationToken: ct);
        else
            await inventory.UpdateOneAsync(session,
                ManagedDeclarationFilters.Address(context.TenantId, operation.Declaration.Address),
                Builders<ManagedDeclaration>.Update.Inc(item => item.MutationFence, 1)
                    .Set(item => item.DesiredHash, desired.DesiredHash)
                    .Set(item => item.PendingRemoval, desired.PendingRemoval),
                cancellationToken: ct);
        return null;
    }

    public async Task<bool> AuthorizeAsync(string tenantId,
        string kind,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken ct,
        GitMutationContext? context = null)
    {
        GitTransactionGuard.Require(session);
        var owner = await inventory.Find(session,
            ManagedDeclarationFilters.Resource(tenantId, kind, resourceId)).FirstOrDefaultAsync(ct);
        if (owner is null)
            return context is null;
        if (context is null || !owner.ToDomain().CanBeMutatedBy(new(context.TenantId, context.SourceId), resourceId))
            return false;

        var run = await runs.TouchAsync(context.ToHandle(), session, ct);
        if (run?.Input is not GitInputAccepted accepted || run.Commit != context.Authority.Commit ||
            !accepted.Snapshot.Resources.Any(item => item.Kind == kind && item.ResourceId == resourceId &&
                                                     item.Name == owner.Name))
            return false;

        await inventory.UpdateOneAsync(session,
            ManagedDeclarationFilters.Resource(tenantId, kind, resourceId),
            Builders<ManagedDeclaration>.Update.Inc(item => item.MutationFence, 1),
            cancellationToken: ct);
        return true;
    }

    // Read-only planning can still look up a name. Applying a snapshot uses accepted references instead.
    public Task<string?> ResolveAsync(
        string tenantId,
        ResourceAddress address,
        IClientSessionHandle session,
        CancellationToken ct) =>
        services.GetRequiredService<DeclarativeProviderRegistry>().Find(address.Kind) is { } provider
            ? provider.ResolveAsync(new(tenantId, address.Name), session, ct)
            : Task.FromResult<string?>(null);
}
