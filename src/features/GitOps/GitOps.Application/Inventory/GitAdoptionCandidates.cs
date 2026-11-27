// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Inventory;

public closed record GitCandidateSearch;

public sealed record GitNamedCandidateSearch(
    string Name
) : GitCandidateSearch;

public sealed record GitRelatedCandidateSearch(
    ResourceAddress Related,
    ResourceAddress Consumer
) : GitCandidateSearch;

public sealed record ReadGitAdoptionCandidates(
    GitSourceIdentity Source,
    string Kind,
    GitCandidateSearch Search,
    DeclarationAdoptionCandidatePageRequest Page
);

public sealed class GitAdoptionCandidates(
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<ManagedDeclaration> inventory,
    DeclarativeProviderRegistry providers,
    MongoRunTransaction transactions
)
{
    public Task<ListAdoptionCandidatesOutcome> ReadAsync(ReadGitAdoptionCandidates request, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<ListAdoptionCandidatesOutcome>(
            async (tx, token) =>
            {
                var tenant = request.Source.TenantId;
                if (!await sources.Find(tx.Session, GitRepositoryFilters.VisibleSource(tenant, request.Source.SourceId))
                        .AnyAsync(token))
                    return tx.Abort(new GitSourceNotFound());

                var provider = providers.Find(request.Kind);
                if (provider is not IDeclarativeResourceLifecycleProvider lifecycle)
                    return tx.Abort(new AdoptionCandidateSearchUnsupported());

                DeclarationAdoptionCandidatePage matches;
                if (request.Search is GitNamedCandidateSearch named)
                {
                    var id = request.Page.Page == 1
                        ? await provider.ResolveAsync(
                            new ResolveDeclaredResourceRequest(tenant, named.Name),
                            tx.Session,
                            token)
                        : null;
                    matches = new DeclarationAdoptionCandidatePage(
                        id is null ? [] : [new DeclarationAdoptionCandidate(id, named.Name, null)],
                        false);
                }
                else
                {
                    if (provider is not IDeclarativeAdoptionCandidateProvider relatedProvider)
                        return tx.Abort(new AdoptionCandidateSearchUnsupported());

                    var query = (GitRelatedCandidateSearch)request.Search;
                    var related = await ResolveAsync(tenant, query.Related, tx.Session, token);
                    var consumer = await ResolveAsync(tenant, query.Consumer, tx.Session, token);
                    matches = related is null || consumer is null
                        ? new DeclarationAdoptionCandidatePage([], false)
                        : await relatedProvider.FindAdoptionCandidatesAsync(
                            new FindDeclarationAdoptionCandidatesRequest(
                                tenant,
                                new DeclarationAdoptionCandidateReferences(related, consumer),
                                request.Page),
                            tx.Session,
                            token);
                }

                var items = new List<AdoptionCandidateView>();
                foreach (var match in matches.Items)
                {
                    var read = await lifecycle.ReadLifecycleAsync(
                        new ReadLifecycleResourceRequest(
                            tenant,
                            new GitLifecycleResourceReference(
                                new ResourceAddress(request.Kind, match.Name ?? ""),
                                match.ResourceId)),
                        tx.Session,
                        token);
                    if (read is not GitLifecycleResourceSnapshot current)
                        continue;

                    var owner = await inventory.Find(
                            tx.Session,
                            ManagedDeclarationFilters.Resource(tenant, request.Kind, match.ResourceId))
                        .FirstOrDefaultAsync(token);
                    var owned = owner is not null && (owner.SourceId != request.Source.SourceId ||
                        owner.Materialization == GitResourceMaterialization.Created);
                    var blocker = owned ? "OwnershipConflict" :
                        current.Presence == GitLifecyclePresence.Deleting ? "ResourceDeleting" : null;
                    items.Add(
                        new AdoptionCandidateView(
                            request.Kind,
                            match.Name,
                            match.ResourceId,
                            owner?.SourceId,
                            blocker is null,
                            blocker,
                            current.Presence,
                            Binding(match.Binding)));
                }

                return tx.Commit(
                    new AdoptionCandidatePage(items, request.Page.Page, request.Page.PageSize, matches.HasMore));
            },
            ct);

    private async Task<GitLifecycleResourceReference?> ResolveAsync(
        string tenant,
        ResourceAddress address,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var provider = providers.Find(address.Kind);
        var id = provider is null
            ? null
            : await provider.ResolveAsync(new ResolveDeclaredResourceRequest(tenant, address.Name), session, ct);
        if (id is null || provider is not IDeclarativeResourceLifecycleProvider lifecycle)
            return null;
        var reference = new GitLifecycleResourceReference(address, id);
        var read = await lifecycle.ReadLifecycleAsync(new ReadLifecycleResourceRequest(tenant, reference), session, ct);
        return read is GitLifecycleResourceSnapshot { Presence: GitLifecyclePresence.Active } ? reference : null;
    }

    private static AdoptionBindingView? Binding(DeclarationAdoptionBindingMetadata? metadata) => metadata is null
        ? null
        : new AdoptionBindingView(
            new AdoptionNamedResourceView(metadata.Secret.Address.Name, metadata.Secret.ResourceId),
            new AdoptionConsumerView(
                metadata.Consumer.Address.Kind,
                metadata.Consumer.Address.Name,
                metadata.Consumer.ResourceId),
            metadata.Usage,
            new AdoptionTargetScopeView(metadata.TargetScope.Mode, metadata.TargetScope.DeploymentIds));
}
