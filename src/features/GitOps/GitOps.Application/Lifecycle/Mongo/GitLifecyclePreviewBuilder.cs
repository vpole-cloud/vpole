using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Inventory.Mongo.Filters;
using Pole.Features.GitOps.Application.Planning.Mongo.Filters;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Lifecycle.Mongo;

public enum GitLifecyclePreviewMode { ExplicitSelection, ReconciliationDiscovery }

public sealed record GitReconciliationPreviewRequest(
    RepositorySource Source,
    GitSynchronizationState Synchronization,
    GitAcceptedSnapshot Snapshot);

public sealed record GitPreviewRejected(IDeclarationFailure Failure);

public sealed record GitPreviewBuilt(GitOperationPreview Preview);

public union BuildLifecyclePreviewOutcome(
    GitPreviewBuilt,
    GitPreviewRejected);

public sealed class GitLifecyclePreviewBuilder(
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitAcceptedReference> references,
    GitLifecycleResources resources,
    GitLifecyclePreviewReferences previewReferences,
    DeclarativeProviderRegistry providers,
    ManifestCompiler compiler,
    TimeProvider clock)
{
    public async Task<BuildLifecyclePreviewOutcome> BuildAsync(RepositorySource source,
        GitSynchronizationState synchronization, GitPreviewIntent intent, GitPreviewInput input,
        IClientSessionHandle session, CancellationToken ct)
        => await BuildCoreAsync(source, synchronization, intent, input,
            intent is GitPreviewResourceChanges or GitPreviewRelocateSource
                ? GitLifecyclePreviewMode.ReconciliationDiscovery :
                GitLifecyclePreviewMode.ExplicitSelection, [], session, ct);

    public Task<BuildLifecyclePreviewOutcome> BuildReconciliationAsync(GitReconciliationPreviewRequest request,
        IClientSessionHandle session, CancellationToken ct) => BuildCoreAsync(request.Source, request.Synchronization,
        new GitPreviewResourceChanges(request.Source.Prune == GitPrunePolicy.Retain
            ? GitPreviewRemovalDisposition.Retain : GitPreviewRemovalDisposition.Remove),
        new GitPreviewRepositoryInput(request.Source.Repository, request.Snapshot),
        GitLifecyclePreviewMode.ReconciliationDiscovery, [], session, ct);

    private async Task<BuildLifecyclePreviewOutcome> BuildCoreAsync(RepositorySource source,
        GitSynchronizationState synchronization, GitPreviewIntent intent, GitPreviewInput input,
        GitLifecyclePreviewMode mode, IReadOnlyList<GitReplacementSelection> discoveredReplacements,
        IClientSessionHandle session, CancellationToken ct)
    {
        if (intent is GitPreviewResourceChanges { Removal: GitPreviewRemovalDisposition.Retain } &&
            source.Prune != GitPrunePolicy.Retain)
            return Reject(new GitLifecycleRetainPolicyRequired());

        var owned = await inventory.Find(session,
            ManagedDeclarationFilters.SourceInventory(source.TenantId, source.Id)).ToListAsync(ct);
        var selected = new Dictionary<string, (GitResourceIdentity Resource, GitLifecycleAction Action,
            GitResourceIdentity? Replacement)>();
        var snapshot = (input as GitPreviewRepositoryInput)?.Snapshot;
        var desired = snapshot?.Resources.ToDictionary(item => (item.Kind, item.Name)) ?? [];
        var originalIds = owned.ToDictionary(item => (item.Kind, item.Name), item => item.ResourceId);

        switch (intent)
        {
            case GitPreviewDetachSource:
                foreach (var item in owned)
                    Select(Identity(item), GitLifecycleAction.Detach, null);
                break;
            case GitPreviewAdoption adoption:
                foreach (var item in adoption.Resources)
                {
                    if (!desired.ContainsKey((item.Kind, item.Name)))
                        return Reject(new DeclarationIdentityChanged());
                    Select(item, GitLifecycleAction.Adopt, null);
                }
                break;
            case GitPreviewRemoval removal:
                foreach (var item in removal.Resources)
                {
                    if (desired.ContainsKey((item.Kind, item.Name)) ||
                        !owned.Any(owner => Identity(owner) == item))
                        return Reject(new DeclarationIdentityChanged());
                    Select(item, GitLifecycleAction.Remove, null);
                }
                break;
            case GitPreviewReplacement replacement:
                foreach (var item in replacement.Resources)
                {
                    if (item.Original.Kind != item.DesiredKind ||
                        !desired.TryGetValue((item.DesiredKind, item.DesiredName), out var next))
                        return Reject(new DeclarationIdentityChanged());
                    Select(item.Original, GitLifecycleAction.Replace,
                        new(next.Kind, next.Name, next.ResourceId));
                }
                break;
            case GitPreviewResourceChanges or GitPreviewRelocateSource:
                var retain = intent is GitPreviewResourceChanges { Removal: GitPreviewRemovalDisposition.Retain } ||
                    intent is GitPreviewRelocateSource && source.Prune == GitPrunePolicy.Retain;
                foreach (var item in owned.Where(item => !desired.ContainsKey((item.Kind, item.Name))))
                    Select(Identity(item), retain ? GitLifecycleAction.Retain : GitLifecycleAction.Remove, null);
                break;
        }

        foreach (var replacement in discoveredReplacements)
        {
            var next = desired[(replacement.DesiredKind, replacement.DesiredName)];
            var id = ObjectId.GenerateNewId().ToString();
            desired[(next.Kind, next.Name)] = next with { ResourceId = id };
            Select(replacement.Original, GitLifecycleAction.Replace, new(next.Kind, next.Name, id));
        }
        if (mode == GitLifecyclePreviewMode.ReconciliationDiscovery)
        {
            foreach (var item in desired.Values.ToArray())
            {
                if (owned.Any(owner => owner.Kind == item.Kind && owner.Name == item.Name))
                    continue;
                // Name lookup detects a conflict; only the reviewed concrete ID can be adopted.
                var id = await providers.Find(item.Kind)!.ResolveAsync(new(source.TenantId, item.Name), session, ct);
                if (id is null)
                    continue;
                desired[(item.Kind, item.Name)] = item with { ResourceId = id };
                Select(new(item.Kind, item.Name, id), GitLifecycleAction.Adopt, null);
            }
        }

        var dependencySets = new Dictionary<(string Owner, GitResourceIdentity Target), GitPreviewDependencySet>();
        var inspected = new HashSet<string>(StringComparer.Ordinal);
        while (selected.Values.FirstOrDefault(item => !inspected.Contains(item.Resource.ResourceId)) is var pending &&
               pending.Resource is not null)
        {
            inspected.Add(pending.Resource.ResourceId);
            if (pending.Action is GitLifecycleAction.Detach or GitLifecycleAction.Adopt or GitLifecycleAction.Retain)
                continue;

            var lifecycleRead = await resources.ReadAsync(source.TenantId, pending.Resource, session, ct);
            if (lifecycleRead is DeclarationReadBlocked rejected)
                return Reject(rejected.Failure);
            var sets = await resources.DependencySetsAsync(source.TenantId, pending.Resource, session, ct);
            foreach (var set in sets)
            {
                dependencySets[(set.Owner, set.Target)] = set;
                foreach (var dependency in set.Dependencies)
                {
                    if (selected.ContainsKey(dependency.Dependent.ResourceId))
                        continue;
                    var owner = await inventory.Find(session, ManagedDeclarationFilters.Resource(source.TenantId,
                        dependency.Dependent.Kind, dependency.Dependent.ResourceId)).FirstOrDefaultAsync(ct);
                    if (owner?.SourceId != source.Id)
                        return Reject(new GitLifecycleDependencyRetained());

                    if (desired.TryGetValue((owner.Kind, owner.Name), out var next))
                    {
                        if (pending.Action != GitLifecycleAction.Replace)
                            return Reject(new GitLifecycleDependencyRetained());
                        var id = ObjectId.GenerateNewId().ToString();
                        desired[(owner.Kind, owner.Name)] = next with { ResourceId = id };
                        Select(Identity(owner), GitLifecycleAction.Replace, new(owner.Kind, owner.Name, id));
                    }
                    else
                        Select(Identity(owner), GitLifecycleAction.Remove, null);
                }
            }
        }

        if (snapshot is not null)
        {
            var byAddress = desired;
            snapshot = snapshot with
            {
                Resources = snapshot.Resources.Select(item => byAddress[(item.Kind, item.Name)]).ToImmutableArray(),
                Operations = snapshot.Operations.Select(item => item with
                {
                    ResourceId = byAddress[(item.Kind, item.Name)].ResourceId
                }).ToImmutableArray()
            };
            input = ((GitPreviewRepositoryInput)input) with { Snapshot = snapshot };
        }

        var collected = snapshot is null ? [] : await PinReferencesAsync(source, snapshot, originalIds, session, ct);
        if (collected is null)
            return Reject(new DeclarationReferenceUnavailable(new("Reference", "unavailable")));

        var pinned = collected.ToList();
        if (snapshot is not null)
        {
            pinned.AddRange(await previewReferences.DerivedAsync(source.TenantId, snapshot, session, ct));
            foreach (var consumer in snapshot.Resources)
            {
                var resolved = await previewReferences.ResolveAsync(source.TenantId, snapshot, consumer, pinned,
                    session, ct);
                if (resolved is DeclarationReadBlocked failed)
                    return Reject(failed.Failure);
                foreach (var reference in ((GitPreviewReferencesSnapshot)resolved.Value).References.Where(reference =>
                             reference.Version is not null || reference.State == GitPreviewReferenceState.Planned))
                {
                    if (snapshot.Resources.Any(item => item.Kind == reference.Address.Kind &&
                            item.Name == reference.Address.Name))
                        continue;
                    var persisted = GitLifecyclePreviewReferences.Persist(consumer.ResourceId, reference);
                    pinned.RemoveAll(item => item.ConsumerResourceId == consumer.ResourceId &&
                        item.Target.Kind == reference.Address.Kind && item.Target.Name == reference.Address.Name);
                    pinned.Add(persisted);
                }
            }
        }

        if (mode == GitLifecyclePreviewMode.ReconciliationDiscovery && snapshot is not null)
        {
            var replacements = new List<GitReplacementSelection>();
            foreach (var owner in owned.Where(owner => owner.Materialization == GitResourceMaterialization.Created &&
                         desired.ContainsKey((owner.Kind, owner.Name)) && !selected.ContainsKey(owner.ResourceId)))
            {
                var declaration = Restore(desired[(owner.Kind, owner.Name)]);
                var preview = await PreviewAdoptionAsync(source.TenantId, declaration, Identity(owner), pinned,
                    snapshot, session, ct);
                if (preview is DeclarationReadBlocked { Failure: DeclarationReplacementRequired } ||
                    preview is GitLifecycleAdoptionPreview projected &&
                    !JsonElement.DeepEquals(projected.Current.Projection.Identity, projected.Desired.Identity))
                    replacements.Add(new(Identity(owner), owner.Kind, owner.Name));
                else if (preview is DeclarationReadBlocked blocked)
                    return Reject(blocked.Failure);
            }
            if (replacements.Count > 0)
                return await BuildCoreAsync(source, synchronization, intent, input, mode,
                    discoveredReplacements.Concat(replacements).ToArray(), session, ct);
        }

        var operations = new List<GitPreviewOperation>();
        foreach (var selectedItem in selected.Values.OrderBy(item => item.Resource.Kind, StringComparer.Ordinal)
                     .ThenBy(item => item.Resource.Name, StringComparer.Ordinal))
        {
            var resource = selectedItem.Resource;
            var owner = await inventory.Find(session,
                ManagedDeclarationFilters.Resource(source.TenantId, resource.Kind, resource.ResourceId))
                .FirstOrDefaultAsync(ct);
            if (owner is not null && (owner.SourceId != source.Id ||
                    selectedItem.Action == GitLifecycleAction.Adopt &&
                    owner.Materialization == GitResourceMaterialization.Created) ||
                selectedItem.Action is GitLifecycleAction.Remove or GitLifecycleAction.Detach && owner is null)
                return Reject(new GitDeclarationOwnershipConflict());

            var read = await resources.ReadAsync(source.TenantId, resource, session, ct);
            if (read is DeclarationReadBlocked blocked)
                return Reject(blocked.Failure);
            if (read is GitLifecycleResourceAbsent && selectedItem.Action == GitLifecycleAction.Adopt)
                return Reject(new DeclarationManagedResourceMissing());
            var current = read.Value as GitLifecycleResourceSnapshot;
            var warnings = Warnings(selectedItem.Action, current?.Protection);
            if (selectedItem.Action is GitLifecycleAction.Remove or GitLifecycleAction.Replace &&
                providers.Find(resource.Kind) is IDeclarativeLifecycleWarningProvider warningProvider)
            {
                var policyWarnings = await warningProvider.ReadLifecycleWarningsAsync(
                    new(source.TenantId, resource.ResourceId, selectedItem.Action), session, ct);
                warnings = warnings.Concat(policyWarnings).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToImmutableArray();
            }
            var changed = Array.Empty<string>();
            var expected = current is null ? null : GitLifecycleResources.Expected(resource, owner?.SourceId, current);
            if (selectedItem.Action == GitLifecycleAction.Adopt)
            {
                var declaration = Restore(desired[(resource.Kind, resource.Name)]);
                var preview = await PreviewAdoptionAsync(source.TenantId, declaration, resource, pinned, snapshot!,
                    session, ct);
                if (preview is DeclarationReadBlocked failed)
                    return Reject(failed.Failure);
                var result = (GitLifecycleAdoptionPreview)preview.Value;
                if (!JsonElement.DeepEquals(result.Current.Projection.Identity, result.Desired.Identity) &&
                    providers.Find(resource.Kind) is not IDeclarativeResourceAdoptionProvider)
                    return Reject(new DeclarationReplacementRequired());
                expected = expected! with
                {
                    Versions = GitLifecyclePreviewReferences.Merge(expected.Versions
                    .Concat(result.Current.Version.Documents.Select(item => new GitResourceVersion(item.DocumentId,
                        item.Revision))).Concat(result.Desired.Dependencies?.Documents.Select(item =>
                        new GitResourceVersion(item.DocumentId, item.Revision)) ?? []))
                };
                changed = DeclarationPlanValues.ChangedFields(new UpdateDeclarationPlan(result.Current, result.Desired))
                    .ToArray();
            }
            operations.Add(new(ActionId(resource), resource, selectedItem.Action,
                expected,
                selectedItem.Replacement, [], changed.ToImmutableArray(), warnings,
                current?.Protection ?? GitLifecycleProtection.ProtectedData));
        }

        // The preview includes every desired operation that confirmation will authorize, not only deletions.
        foreach (var item in desired.Values.OrderBy(item => item.Kind, StringComparer.Ordinal)
                     .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            if (selected.Values.Any(value => value.Resource.Kind == item.Kind && value.Resource.Name == item.Name ||
                    value.Replacement is { } replacement && replacement.Kind == item.Kind &&
                    replacement.Name == item.Name))
                continue;
            var resource = new GitResourceIdentity(item.Kind, item.Name, item.ResourceId);
            var owner = owned.SingleOrDefault(value => value.Kind == item.Kind && value.Name == item.Name);
            var read = await resources.ReadAsync(source.TenantId, resource, session, ct);
            if (read is DeclarationReadBlocked blocked)
                return Reject(blocked.Failure);
            if (read is GitLifecycleResourceAbsent && owner?.Materialization == GitResourceMaterialization.Created)
                return Reject(new DeclarationManagedResourceMissing());
            if (owner is null && read is GitLifecycleResourceSnapshot)
                return Reject(new DeclarationAdoptionRequired());
            var changed = Array.Empty<string>();
            if (owner is not null && read is GitLifecycleResourceSnapshot)
            {
                var projected = await PreviewAdoptionAsync(source.TenantId, Restore(item), resource, pinned,
                    snapshot!, session, ct);
                if (projected is DeclarationReadBlocked failed)
                    return Reject(failed.Failure);
                var preview = (GitLifecycleAdoptionPreview)projected.Value;
                if (!JsonElement.DeepEquals(preview.Current.Projection.Identity, preview.Desired.Identity))
                    return Reject(new DeclarationReplacementRequired());
                changed = DeclarationPlanValues.ChangedFields(new UpdateDeclarationPlan(preview.Current,
                    preview.Desired)).ToArray();
            }
            operations.Add(new(ActionId(resource), resource, GitLifecycleAction.Apply,
                read.Value is GitLifecycleResourceSnapshot current
                    ? GitLifecycleResources.Expected(resource, owner?.SourceId, current) : null,
                null, [], changed.ToImmutableArray(), [], read.Value is GitLifecycleResourceSnapshot existing
                    ? existing.Protection : GitLifecycleProtection.ConfigurationOnly));
        }

        var allByResource = operations.ToDictionary(item => item.Resource.ResourceId);
        operations = operations.Select(operation => operation with
        {
            Dependencies = dependencySets.Values.Where(set => set.Target == operation.Resource)
                .SelectMany(set => set.Dependencies)
                .Where(dependency => dependency.Order == GitLifecycleDependencyOrder.BeforeRemoval)
                .Select(dependency => allByResource[dependency.Dependent.ResourceId].Id)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray()
        }).ToList();
        var previewSource = new GitPreviewSourceStamp(new(source.TenantId, source.Id), source.Revision,
            synchronization.InventoryRevision, input);
        if (operations.Count == 0 && intent is not (GitPreviewDetachSource or GitPreviewRelocateSource))
            return Reject(new GitLifecycleNoChanges());
        foreach (var operation in operations.ToArray())
        {
            var current = await previewReferences.PreconditionsAsync(new(previewSource, operation, pinned),
                session, ct);
            if (operation.Expected is not null && current is null)
                return Reject(new DeclarationPreconditionsChanged());
            operations[operations.IndexOf(operation)] = operation with { Expected = current };
        }
        if (!GitLifecycleRemovalGraph.IsAcyclic(operations, dependencySets.Values))
            return Reject(new GitLifecycleDependencyCycle());
        return new GitPreviewBuilt(GitOperationPreviewFactory.Create(ObjectId.GenerateNewId().ToString(), previewSource,
            intent, operations, clock.GetUtcNow().UtcDateTime, pinned, dependencySets.Values));

        void Select(GitResourceIdentity resource, GitLifecycleAction action, GitResourceIdentity? replacement) =>
            selected[resource.ResourceId] = (resource, action, replacement);
    }

    private async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(string tenant,
        CompiledDeclaration declaration, GitResourceIdentity resource, IReadOnlyList<GitPreviewPinnedReference> pinned,
        GitAcceptedSnapshot snapshot, IClientSessionHandle session, CancellationToken ct)
    {
        var provider = providers.Find(resource.Kind)!;
        var resolved = GitLifecyclePreviewReferences.Arguments(snapshot, resource.ResourceId, pinned).ToArray();
        var facts = provider.Facts(declaration.Spec);
        var dependencies = facts.References.Select(item => item.Target)
            .Concat(facts.Scope is { } scope ? [scope] : [])
            .Concat(declaration.Steps.SelectMany(item => item.Dependencies).Select(item => item.Resource)).ToHashSet();
        for (var index = 0; index < resolved.Length; index++)
        {
            var reference = resolved[index];
            if (reference.State == GitPreviewReferenceState.Planned || !dependencies.Contains(reference.Address) ||
                !snapshot.Resources.Any(item => item.Kind == reference.Address.Kind &&
                    item.Name == reference.Address.Name))
                continue;
            if (await resources.ReadAsync(tenant, new(reference.Address.Kind, reference.Address.Name,
                    reference.ResourceId), session, ct) is GitLifecycleResourceAbsent)
                resolved[index] = reference with { State = GitPreviewReferenceState.Planned };
        }
        if (provider is IDeclarativeAdoptionPreviewProvider custom)
            return await custom.PreviewAdoptionAsync(new(tenant, declaration, resource.ResourceId, resolved),
                session, ct);
        if (provider is not IDeclarativeResourcePlanningProvider planning)
            return new DeclarationReadBlocked(new GitLifecycleUnsupported());

        var current = await planning.ReadCurrentAsync(new(tenant, declaration.Address), session, ct);
        var desired = await planning.ProjectDesiredAsync(new(tenant, declaration), session, ct);
        if (current is DeclarationReadBlocked blocked)
            return blocked;
        if (desired is DeclarationReadBlocked failed)
            return failed;
        if (current is not CurrentDeclaration found || found.ResourceId != resource.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        var projection = (DeclarationProjection)desired.Value;
        return JsonElement.DeepEquals(found.Projection.Identity, projection.Identity)
            ? new GitLifecycleAdoptionPreview(found, projection, [])
            : new DeclarationReadBlocked(new DeclarationReplacementRequired());
    }

    private async Task<GitPreviewPinnedReference[]?> PinReferencesAsync(RepositorySource source,
        GitAcceptedSnapshot snapshot, IReadOnlyDictionary<(string Kind, string Name), string> originalIds,
        IClientSessionHandle session, CancellationToken ct)
    {
        var result = new List<GitPreviewPinnedReference>();
        var internalAddresses = snapshot.Resources.Select(item => (item.Kind, item.Name)).ToHashSet();
        foreach (var item in snapshot.Resources)
        {
            var declaration = Restore(item);
            var facts = providers.Find(item.Kind)!.Facts(declaration.Spec);
            var addresses = facts.References.Select(value => value.Target)
                .Concat(facts.Scope is { } scope ? [scope] : [])
                .Concat(declaration.Steps.SelectMany(step => step.Dependencies).Select(value => value.Resource))
                .Distinct();
            foreach (var address in addresses.Where(address =>
                         !internalAddresses.Contains((address.Kind, address.Name))))
            {
                var consumer = originalIds.GetValueOrDefault((item.Kind, item.Name)) ?? item.ResourceId;
                var old = await references.Find(session, GitAcceptedReferenceFilters.Identity(
                    GitAcceptedReferenceFilters.Key(new(source.TenantId, source.Id), consumer,
                        address.Kind, address.Name)))
                    .FirstOrDefaultAsync(ct);
                var provider = providers.Find(address.Kind);
                var id = old?.Target.ResourceId ?? (provider is null ? null :
                    await provider.ResolveAsync(new(source.TenantId, address.Name), session, ct));
                if (id is null)
                    return null;
                var resource = new GitResourceIdentity(address.Kind, address.Name, id);
                var read = await resources.ReadAsync(source.TenantId, resource, session, ct);
                if (read is not GitLifecycleResourceSnapshot found)
                    return null;
                result.Add(new(item.ResourceId, resource, found.Version.Documents.Select(version =>
                    new GitResourceVersion(version.DocumentId, version.Revision)).ToImmutableArray(),
                    GitPreviewReferenceState.Existing));
            }
        }
        return result.ToArray();
    }

    private CompiledDeclaration Restore(AcceptedDeclaration item) =>
        compiler.Restore(SnapshotSchema.ApiVersion,
                [new AcceptedManifestDeclaration(item.Kind, item.Name, item.SpecJson)])
            .Declarations.Single();

    private static ImmutableArray<string> Warnings(GitLifecycleAction action, GitLifecycleProtection? protection)
    {
        var warnings = new HashSet<PreviewWarning>();
        if (action == GitLifecycleAction.Replace)
            warnings.UnionWith([PreviewWarning.IdentityWillChange, PreviewWarning.Downtime,
                PreviewWarning.NoAutomaticDataMigration]);
        if (action is GitLifecycleAction.Remove or GitLifecycleAction.Replace &&
            protection == GitLifecycleProtection.ProtectedData)
            warnings.UnionWith([PreviewWarning.ProtectedData, PreviewWarning.DataMayBeLost,
                PreviewWarning.NoAutomaticDataMigration]);
        return warnings.Select(item => item.ToString()).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private enum PreviewWarning
    {
        ProtectedData,
        DataMayBeLost,
        NoAutomaticDataMigration,
        IdentityWillChange,
        Downtime
    }

    private static string ActionId(GitResourceIdentity resource) => $"{resource.Kind}/{resource.ResourceId}";

    private static GitResourceIdentity Identity(ManagedDeclaration item) => new(item.Kind, item.Name, item.ResourceId);

    private static GitPreviewRejected Reject(IDeclarationFailure failure) => new(failure);
}
