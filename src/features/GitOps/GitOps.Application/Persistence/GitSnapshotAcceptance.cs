using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

public sealed class GitSnapshotAcceptance(IMongoCollection<ManagedDeclaration> inventory)
{
    public async Task<GitAcceptedSnapshot> BuildAsync(
        GitWorkClaim claim,
        CompiledSnapshot compiled,
        CancellationToken ct)
    {
        if (!compiled.Valid || !DeclarationPlan.TryOrder(compiled.Declarations, out var plan))
            throw new InvalidOperationException("Only a complete validated operation graph may be accepted.");
        if (claim.Run.Commit is not { } commit)
            throw new InvalidOperationException("Snapshot acceptance requires the persisted immutable commit.");

        var resources = new List<AcceptedDeclaration>();
        foreach (var declaration in compiled.Declarations)
        {
            var current = await inventory.Find(ManagedDeclarationFilters.Address(
                claim.Source.TenantId,
                declaration.Address)).FirstOrDefaultAsync(ct);
            resources.Add(new(declaration.Address.Kind,
                declaration.Address.Name,
                declaration.Hash,
                declaration.Spec.GetRawText(),
                current?.ResourceId ?? ObjectId.GenerateNewId().ToString()));
        }

        var byAddress = resources.ToDictionary(item => (item.Kind, item.Name));
        var operations = plan.Select(item =>
        {
            var resource = byAddress[(item.Declaration.Address.Kind, item.Declaration.Address.Name)];
            return new GitAcceptedOperation(resource.Kind, resource.Name, resource.ResourceId, item.Phase);
        }).ToImmutableArray();
        var hashInput = resources.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal).Select(item => new { item.Kind, item.Name, item.Hash });
        var desiredHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hashInput)));
        return new(commit, desiredHash, SnapshotSchema.ApiVersion, resources.ToImmutableArray(), operations);
    }
}
