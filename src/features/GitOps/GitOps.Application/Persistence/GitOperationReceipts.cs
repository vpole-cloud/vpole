using Pole.Features.GitOps.Domain;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Application.Persistence;

internal sealed class GitOperationReceipts(IMongoCollection<GitOperationReceipt> receipts, IServiceProvider services)
{
    public string? DiagnosticCode(
        GitOperation operation,
        GitOperationResult result) => DeclarationFailureDiagnostics.Code(
        result.Failure,
        services.GetRequiredService<DeclarativeProviderRegistry>()
            .Find(operation.Declaration.Address.Kind)
        ?? throw new InvalidOperationException("The declaration provider is no longer registered."));

    public Task StoreAsync(
        GitOperation operation,
        GitOperationResult result,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var context = operation.Context;
        var address = operation.Declaration.Address;
        var receipt = new GitOperationReceipt(
            ReceiptId(operation),
            context.TenantId,
            context.SourceId,
            context.RunId,
            context.Fence,
            address.Kind,
            address.Name,
            operation.Phase.ToString(),
            result.ResourceId,
            operation.Declaration.Hash,
            DiagnosticCode(
                operation,
                result),
            result.Changed,
            operation.Plan.Revision);

        return receipts.ReplaceOneAsync(
            session,
            GitOperationReceiptFilters.Identity(receipt.Id),
            receipt,
            new ReplaceOptions { IsUpsert = true },
            ct);
    }

    internal static string ReceiptId(GitOperation operation) => ReceiptId(operation.Input);

    internal static string ReceiptId(GitOperationInput operation)
    {
        var context = operation.Context;
        var address = operation.Declaration.Address;

        return $"{context.TenantId}/{context.SourceId}/{context.RunId}/{address.Kind}/{address.Name}/{operation.Phase}";
    }
}
