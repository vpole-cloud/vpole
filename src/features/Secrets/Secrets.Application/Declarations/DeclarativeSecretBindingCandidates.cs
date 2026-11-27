// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Declarations;

internal sealed class SecretBindingCandidates(
    IMongoCollection<Secret> secrets,
    SecretBindingConsumerCatalog consumers
)
{
    public async Task<DeclarationAdoptionCandidatePage> FindAdoptionCandidatesAsync(
        FindDeclarationAdoptionCandidatesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var related = request.References.Related;
        var consumer = request.References.Consumer;
        if (related.Address.Kind != "Secret" || !consumers.TryGet(consumer.Address.Kind, out var capability))
            return new DeclarationAdoptionCandidatePage([], false);
        var secret = await secrets.Find(session, SecretFilters.Identity(request.TenantId, related.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (secret is null || secret.Name != related.Address.Name || secret.Management is ProviderManagedSecret)
            return new DeclarationAdoptionCandidatePage([], false);

        var bindings = secret.Bindings
            .Where(item => item.Consumer.ResourceType == capability.ResourceType.Value &&
                item.Consumer.ResourceId == consumer.ResourceId)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Skip((request.Page.Page - 1) * request.Page.PageSize)
            .Take(request.Page.PageSize + 1)
            .ToArray();
        return new DeclarationAdoptionCandidatePage(
            bindings.Take(request.Page.PageSize)
                .Select(item => new DeclarationAdoptionCandidate(
                    item.Id,
                    item.DeclarationName,
                    new DeclarationAdoptionBindingMetadata(
                        related,
                        consumer,
                        item.Usage.ToString(),
                        item.Selection switch
                        {
                            AllSecretDeployments => new DeclarationAdoptionCandidateTargetScope("All", []),
                            SelectedSecretDeployments selected => new DeclarationAdoptionCandidateTargetScope(
                                "Selected",
                                selected.DeploymentIds),
                            _ => throw new ArgumentOutOfRangeException(nameof(item.Selection)),
                        })))
                .ToArray(),
            bindings.Length > request.Page.PageSize);
    }
}
