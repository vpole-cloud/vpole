using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Application.Mongo;
using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Lifecycle;

public sealed record ReadGitPreviewPreconditions(
    GitPreviewSourceStamp Source,
    GitPreviewOperation Operation,
    IReadOnlyList<GitPreviewPinnedReference> References);

public sealed record RefreshGitPreviewReferenceVersionsRequest(
    string TenantId,
    IReadOnlyList<GitPreviewPinnedReference> References);

public sealed class GitLifecyclePreviewReferences(
    DeclarativeProviderRegistry providers,
    GitLifecycleResources resources,
    ManifestCompiler compiler,
    IMongoCollection<GitAcceptedReference> references,
    IMongoCollection<ManagedDeclaration> inventory)
{
    public async Task<ReadPreviewReferencesOutcome> ResolveAsync(string tenant, GitAcceptedSnapshot snapshot,
        AcceptedDeclaration consumer, IReadOnlyList<GitPreviewPinnedReference> pinned,
        IClientSessionHandle session, CancellationToken ct)
    {
        var accepted = Arguments(snapshot, consumer.ResourceId, pinned).ToList();
        var original = await inventory.Find(session, ManagedDeclarationFilters.Address(tenant,
            new(consumer.Kind, consumer.Name))).FirstOrDefaultAsync(ct);
        if (original is not null)
        {
            var old = await references.Find(session,
                GitAcceptedReferenceFilters.Consumer(new(tenant, original.SourceId), original.ResourceId))
                .ToListAsync(ct);
            foreach (var value in old.Where(value => !accepted.Any(item => item.Address.Kind == value.Target.Kind &&
                         item.Address.Name == value.Target.Name)))
                accepted.Add(new(new(value.Target.Kind, value.Target.Name), value.Target.ResourceId));
        }
        return providers.Find(consumer.Kind) is IDeclarativePreviewReferenceProvider provider
            ? await provider.ReadPreviewReferencesAsync(new(tenant, Restore(consumer), accepted), session, ct)
            : new GitPreviewReferencesSnapshot(accepted);
    }

    public async Task<IReadOnlyList<GitPreviewPinnedReference>> DerivedAsync(string tenant,
        GitAcceptedSnapshot snapshot,
        IClientSessionHandle session, CancellationToken ct)
    {
        var result = new List<GitPreviewPinnedReference>();
        foreach (var consumer in snapshot.Resources)
        {
            if (providers.Find(consumer.Kind) is not IDeclarativeDerivedIdentityProvider provider)
                continue;
            var selected = await provider.ReserveDerivedIdentitiesAsync(new(tenant,
                new(new(consumer.Kind, consumer.Name), consumer.ResourceId)), session, ct);
            result.AddRange(selected.Select(reference => Persist(consumer.ResourceId, reference)));
        }
        return result;
    }

    // Capture only after approved owner writes. Approval validation still uses reviewed/receipt-advanced versions.
    public async Task<IReadOnlyList<GitPreviewPinnedReference>> RefreshVersionsAsync(
        RefreshGitPreviewReferenceVersionsRequest request, IClientSessionHandle session, CancellationToken ct)
    {
        var refreshed = request.References.ToArray();
        for (var index = 0; index < refreshed.Length; index++)
        {
            var reference = refreshed[index];
            if (reference.State != GitPreviewReferenceState.Existing ||
                providers.Find(reference.Target.Kind) is not IDeclarativeResourceLifecycleProvider)
                continue;

            var current = await resources.ReadAsync(request.TenantId, reference.Target, session, ct);
            if (current is GitLifecycleResourceSnapshot snapshot)
                refreshed[index] = reference with
                {
                    Versions = snapshot.Version.Documents.Select(item =>
                        new GitResourceVersion(item.DocumentId, item.Revision)).ToImmutableArray()
                };
        }
        return refreshed;
    }

    public async Task<bool> ValidateAsync(GitPreviewSourceStamp source,
        IReadOnlyList<GitPreviewPinnedReference> expected,
        IClientSessionHandle session, CancellationToken ct)
    {
        if (source.Input is not GitPreviewRepositoryInput repository)
            return expected.Count == 0;
        foreach (var consumerGroup in expected.GroupBy(item => item.ConsumerResourceId))
        {
            var consumer = repository.Snapshot.Resources.Single(item => item.ResourceId == consumerGroup.Key);
            var current = new List<GitPreviewResolvedReference>();
            if (providers.Find(consumer.Kind) is IDeclarativePreviewReferenceProvider provider)
            {
                var read = await provider.ReadPreviewReferencesAsync(new(source.Source.TenantId, Restore(consumer),
                    Arguments(repository.Snapshot, consumer.ResourceId, expected)), session, ct);
                if (read is not GitPreviewReferencesSnapshot resolved)
                    return false;
                current.AddRange(resolved.References);
            }
            if (providers.Find(consumer.Kind) is IDeclarativeDerivedIdentityProvider derived &&
                consumerGroup.Any(item => item.State == GitPreviewReferenceState.Existing))
                current.AddRange(await derived.ReserveDerivedIdentitiesAsync(new(source.Source.TenantId,
                    new(new(consumer.Kind, consumer.Name), consumer.ResourceId)), session, ct));
            foreach (var reference in consumerGroup)
            {
                if (reference.State == GitPreviewReferenceState.Planned)
                    continue;
                if (providers.Find(reference.Target.Kind) is IDeclarativeResourceLifecycleProvider)
                {
                    var actual = await resources.ReadAsync(source.Source.TenantId, reference.Target, session, ct);
                    if (actual is not GitLifecycleResourceSnapshot live || !Same(reference.Versions, live.Version))
                        return false;
                    continue;
                }
                var address = new ResourceAddress(reference.Target.Kind, reference.Target.Name);
                var found = current.SingleOrDefault(item => item.Address == address);
                if (found?.Version is { } version)
                {
                    if (found.ResourceId != reference.Target.ResourceId || !Same(reference.Versions, version))
                        return false;
                    continue;
                }
                var read = await resources.ReadAsync(source.Source.TenantId, reference.Target, session, ct);
                if (read is not GitLifecycleResourceSnapshot resource || !Same(reference.Versions, resource.Version))
                    return false;
            }
        }
        return true;
    }

    public async Task<GitResourcePreconditions?> PreconditionsAsync(ReadGitPreviewPreconditions request,
        IClientSessionHandle session, CancellationToken ct)
    {
        var tenant = request.Source.Source.TenantId;
        var resource = request.Operation.Resource;
        var read = await resources.ReadAsync(tenant, resource, session, ct);
        if (read is not GitLifecycleResourceSnapshot current)
            return null;
        var owner = await inventory.Find(session, ManagedDeclarationFilters.Resource(tenant, resource.Kind,
            resource.ResourceId)).FirstOrDefaultAsync(ct);
        var expected = GitLifecycleResources.Expected(resource, owner?.SourceId, current);
        if (current.Presence == GitLifecyclePresence.Active &&
            providers.Find(resource.Kind) is IDeclarativeResourcePlanningProvider planning)
        {
            var managed = await planning.ReadCurrentAsync(new(tenant, new(resource.Kind, resource.Name)), session, ct);
            if (managed is CurrentDeclaration found && found.ResourceId == resource.ResourceId)
                expected = expected with
                {
                    ConfigurationHash = DeclarationPlanValues.Hash(DeclarationPlanValues.Json(found.Projection)),
                    Versions = current.VersionSemantics == GitLifecycleVersionSemantics.ManagedConfiguration &&
                        request.Operation.Action is GitLifecycleAction.Remove or GitLifecycleAction.Replace or
                            GitLifecycleAction.Detach or GitLifecycleAction.Retain
                        ? expected.Versions
                        : Merge(expected.Versions.Concat(found.Version.Documents.Select(item =>
                            new GitResourceVersion(item.DocumentId, item.Revision))))
                };
        }
        if (request.Operation.Action != GitLifecycleAction.Adopt ||
            request.Source.Input is not GitPreviewRepositoryInput input ||
            providers.Find(resource.Kind) is not IDeclarativeAdoptionPreviewProvider provider)
            return expected;
        var consumer = input.Snapshot.Resources.Single(item => item.ResourceId == resource.ResourceId);
        var preview = await provider.PreviewAdoptionAsync(new(tenant, Restore(consumer), resource.ResourceId,
            Arguments(input.Snapshot, resource.ResourceId, request.References)), session, ct);
        if (preview is not GitLifecycleAdoptionPreview projected)
            return null;
        var versions = expected.Versions.Concat(projected.Current.Version.Documents.Select(item =>
                new GitResourceVersion(item.DocumentId, item.Revision)))
            .Concat(projected.Desired.Dependencies?.Documents.Select(item =>
                new GitResourceVersion(item.DocumentId, item.Revision)) ?? []);
        return expected with
        {
            Versions = Merge(versions),
            ConfigurationHash = DeclarationPlanValues.Hash(DeclarationPlanValues.Json(projected.Current.Projection))
        };
    }

    public static IReadOnlyList<GitPreviewResolvedReference> Arguments(GitAcceptedSnapshot snapshot, string consumer,
        IReadOnlyList<GitPreviewPinnedReference> pinned) => snapshot.Resources.Select(item =>
            new GitPreviewResolvedReference(new(item.Kind, item.Name), item.ResourceId))
        .Concat(pinned.Where(item => item.ConsumerResourceId == consumer ||
            item.State == GitPreviewReferenceState.Planned)
            .Select(item => new GitPreviewResolvedReference(new(item.Target.Kind, item.Target.Name),
                item.Target.ResourceId,
                new(item.Versions.Select(version => new DeclarationDocumentVersion(version.DocumentId,
                    version.Revision))
                    .ToArray()), item.State)))
        .DistinctBy(item => (item.Address, item.ResourceId)).ToArray();

    public static GitPreviewPinnedReference Persist(string consumer, GitPreviewResolvedReference reference) =>
        new(consumer, new(reference.Address.Kind, reference.Address.Name, reference.ResourceId),
            reference.Version?.Documents.Select(item => new GitResourceVersion(item.DocumentId, item.Revision))
                .ToImmutableArray() ?? [], reference.State);

    public static ImmutableArray<GitResourceVersion> Merge(IEnumerable<GitResourceVersion> versions) =>
        versions.GroupBy(item => item.DocumentId, StringComparer.Ordinal).Select(group => group.Distinct().Single())
            .OrderBy(item => item.DocumentId, StringComparer.Ordinal).ToImmutableArray();

    private CompiledDeclaration Restore(AcceptedDeclaration consumer) => compiler.Restore(SnapshotSchema.ApiVersion,
        [new AcceptedManifestDeclaration(consumer.Kind, consumer.Name, consumer.SpecJson)]).Declarations.Single();

    private static bool Same(IEnumerable<GitResourceVersion> expected, DeclarationVersionToken current) =>
        expected.OrderBy(item => item.DocumentId, StringComparer.Ordinal).SequenceEqual(current.Documents.Select(item =>
            new GitResourceVersion(item.DocumentId, item.Revision)).OrderBy(item => item.DocumentId,
                StringComparer.Ordinal));
}
