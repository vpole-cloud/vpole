using MongoDB.Driver;
using Pole.Features.GitOps.Application.Inventory.Mongo.Filters;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace Pole.Features.GitOps.Application.Runs.Mongo;

internal sealed class GitDeclarationCompletion(
    IMongoCollection<ManagedDeclaration> inventory,
    GitOperationReceipts receipts,
    IServiceProvider services)
{
    public async Task<GitOperationResult> StoreAsync(
        GitOperation operation,
        GitOperationResult result,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        GitTransactionGuard.Require(session);
        var final = operation.Phase == operation.Declaration.Steps.Last().Phase;
        DeclarationProjection? observed = null;
        if (result.Applied)
        {
            var provider = services.GetRequiredService<DeclarativeProviderRegistry>()
                .Find(operation.Declaration.Address.Kind) as IDeclarativeResourcePlanningProvider;
            if (provider is null)
                return new(operation.ResourceId,
                    new DeclarationPlanningUnsupported(operation.Declaration.Address.Kind));

            var current = await provider.ReadCurrentAsync(new(operation.Context.TenantId,
                operation.Declaration.Address, operation.Input), session, ct);
            if (current is not CurrentDeclaration found || found.ResourceId != operation.ResourceId ||
                final && found.State != DeclarationConfigurationState.Configured ||
                !DeclarationPlanValues.SameProjection(found.Projection,
                    DeclarationPlanValues.Desired(operation.Plan.Decision)))
                return new(operation.ResourceId, new DeclarationPostconditionFailed());
            observed = found.Projection;
        }

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

        var desired = DeclarationPlanValues.Desired(operation.Plan.Decision);
        var desiredJson = desired is null ? null : DeclarationPlanValues.Json(desired);
        update = update.Set(item => item.Projections.Desired, desiredJson is null
            ? null : new GitManagedConfiguration(DeclarationPlanValues.Hash(desiredJson), desiredJson));
        if (observed is not null)
        {
            update = update.Set(item => item.Materialization, GitResourceMaterialization.Created);
            if (final)
            {
                var appliedJson = DeclarationPlanValues.Json(observed);
                update = update.Set(item => item.Projections.Applied,
                    new GitManagedConfiguration(DeclarationPlanValues.Hash(appliedJson), appliedJson));
            }
        }

        await inventory.UpdateOneAsync(
            session,
            ManagedDeclarationFilters.Address(operation.Context.TenantId, operation.Declaration.Address),
            update,
            cancellationToken: ct);
        await receipts.StoreAsync(operation, result, session, ct);
        return result;
    }
}
