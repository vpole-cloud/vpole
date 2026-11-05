using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Planning;

public sealed record GitSnapshotCandidate(
    string TenantId,
    string Commit,
    CompiledSnapshot Compiled,
    IReadOnlyDictionary<ResourceAddress, string> ResourceIds);

public sealed class GitSnapshotAcceptance(IMongoCollection<ManagedDeclaration> inventory)
{
    public async Task<GitAcceptedSnapshot> BuildAsync(
        GitWorkClaim claim,
        CompiledSnapshot compiled,
        CancellationToken ct) => await BuildAsync(new(claim.Source.TenantId,
        claim.Run.Commit ?? throw new InvalidOperationException("Snapshot acceptance requires a pinned commit."),
        compiled, new Dictionary<ResourceAddress, string>()), ct);

    public async Task<GitAcceptedSnapshot> BuildAsync(GitSnapshotCandidate candidate, CancellationToken ct)
    {
        var compiled = candidate.Compiled;
        var commit = candidate.Commit;
        if (!compiled.Valid || !DeclarationPlan.TryOrder(compiled.Declarations, out var plan))
            throw new InvalidOperationException("Only a complete validated operation graph may be accepted.");

        var resources = new List<AcceptedDeclaration>();
        foreach (var declaration in compiled.Declarations)
        {
            var current = await inventory.Find(ManagedDeclarationFilters.Address(
                candidate.TenantId,
                declaration.Address)).FirstOrDefaultAsync(ct);
            resources.Add(new(declaration.Address.Kind,
                declaration.Address.Name,
                declaration.Hash,
                declaration.Spec.GetRawText(),
                candidate.ResourceIds.TryGetValue(declaration.Address, out var selected)
                    ? selected : current?.ResourceId ?? ObjectId.GenerateNewId().ToString()));
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
