using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

internal sealed class GitDeclarationCompletion(
    IMongoCollection<ManagedDeclaration> inventory,
    GitOperationReceipts receipts)
{
    public async Task StoreAsync(
        GitOperation operation,
        GitOperationResult result,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        GitTransactionGuard.Require(session);
        var final = operation.Phase == operation.Declaration.Steps.Last().Phase;
        var update = Builders<ManagedDeclaration>.Update.Set(
            item => item.Blocker,
            receipts.DiagnosticCode(operation, result))
            .Set(
            item => item.LastRunId,
            operation.Context.RunId).Set(
            item => item.LastPhase,
            operation.Phase.ToString());
        var transition = new DeclarationCompletion(operation.Declaration.Hash, final, result.Applied);

        if (transition.AppliedHashUpdate is { } appliedHash)
            update = update.Set(item => item.AppliedHash, appliedHash);

        await inventory.UpdateOneAsync(
            session,
            ManagedDeclarationFilters.Address(operation.Context.TenantId, operation.Declaration.Address),
            update,
            cancellationToken: ct);
        await receipts.StoreAsync(operation, result, session, ct);
    }
}
