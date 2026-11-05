using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Declarations;

public sealed record SecretDeclaration
{
    public required string ResourceGroup { get; init; }

    public string? IsolationScope { get; init; }

    public string Purpose { get; init; } = "";

    public required string[] Keys { get; init; }
}

internal sealed class DeclarativeSecretProvider(
    IMongoCollection<Secret> secrets,
    IGitOpsMutations git,
    LoadIsolationScopePlanningSnapshot loadPlanningScope,
    FenceIsolationScopePlanningSnapshot fencePlanningScope,
    LoadResourceRegistrationPlanningSnapshot loadRegistration,
    SecretPlanningFence planningFence,
    AdmitResourceScopeReference admitScope,
    IResourceRegistrar registrar,
    MongoDeleteStoredSecret deletion,
    StoreSecretChange publish,
    TimeProvider clock) : ITransactionalDeclarativeResourceProvider, IDeclarativeResourceLifecycleProvider,
    IDeclarativeRegisteredResourceProvider, IDeclarativeLifecycleWarningProvider,
    IDeclarativeRegistrationOwnershipProvider, IDeclarativeAdoptionPreviewProvider
{
    public string Kind => "Secret";
    public ResourceTypeId ResourceType => SecretResource.Type;

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        SecretDeclarationRejected rejected => rejected.Reason switch
        {
            SecretDeclarationError.ResourceGroupUnavailable => "ResourceGroupUnavailable",
            SecretDeclarationError.ExplicitAdoptionRequired => "ExplicitAdoptionRequired",
            SecretDeclarationError.ImmutablePlacementChange => "ImmutablePlacementChange",
            SecretDeclarationError.ResourceScopeUnavailable => "ResourceScopeUnavailable",
            SecretDeclarationError.ResourceRegistrationUnavailable => "ResourceRegistrationUnavailable",
            SecretDeclarationError.SecretKeysInUse => "SecretKeysInUse",
            SecretDeclarationError.ProviderManagedSecret => "ProviderManagedSecret",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretDeclaration>(spec);

        return new(new("ResourceGroup", value.ResourceGroup), value.Keys,
            value.IsolationScope is null ? [] :
                [new(new("IsolationScope", value.IsolationScope), "spec.isolationScope")], [], []);
    }

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup)
                                && (value.IsolationScope is null ||
                                    DeclarationNames.Valid(value.IsolationScope) && value.IsolationScope != "default")
                                && value.Purpose is { Length: <= SecretDeclarationRules.MaximumPurposeLength } &&
                                value.Keys is { Length: > 0 and <= SecretKeyReferenceRules.MaximumKeys } && value.Keys
                                    .Distinct()
                                    .Count() == value.Keys.Length &&
                                value.Keys.All(SecretKeyReferenceRules.ValidKey),
            DeclarationFieldPaths.Spec);

        return DeclarationJson.Write(value with
        {
            Keys = value.Keys.Order(StringComparer.Ordinal).ToArray(),
            Purpose = value.Purpose.Trim()
        });
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretDeclaration>(spec);
        return
        [
            new(DeclarationPhase.Prepare,
            [
                new(new("ResourceGroup", value.ResourceGroup), DeclarationPhase.Prepare),
                .. value.IsolationScope is null ? Array.Empty<DeclarationDependency>() :
                    [new(new("IsolationScope", value.IsolationScope), DeclarationPhase.Prepare)]
            ])
        ];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await secrets.Find(
            session,
            SecretFilters.TechnicalName(request.TenantId, request.Name)).FirstOrDefaultAsync(ct))?.Id;

    public Task<GitOperationResult> ApplyAsync(
        GitOperation operation,
        CancellationToken ct) => git.ExecuteAsync(operation,
        async (
            session,
            token) =>
        {
            var context = operation.Context;
            var spec = DeclarationJson.Read<SecretDeclaration>(operation.Declaration.Spec);
            var owned = await AcceptedScopeAsync(operation.Input, session, token);
            if (owned is null)
                return new(operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ResourceScopeUnavailable));

            var groupId = owned.ResourceGroupId;
            var current = await secrets.Find(session,
                SecretFilters.Identity(context.TenantId, operation.ResourceId)).FirstOrDefaultAsync(token);
            current ??= await secrets.Find(session,
                SecretFilters.TechnicalName(context.TenantId, operation.Declaration.Address.Name))
                .FirstOrDefaultAsync(token);

            if (current?.Management is ProviderManagedSecret)
                return new(operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ProviderManagedSecret));

            if (current is not null && current.Id != operation.ResourceId)
                return new(
                    operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ExplicitAdoptionRequired));

            if (current is not null && (current.ResourceGroupId != groupId
                                        || current.IsolationScopeId != owned.IsolationScopeId))
                return new(
                    current.Id,
                    new SecretDeclarationRejected(SecretDeclarationError.ImmutablePlacementChange));

            if (await admitScope(
                    new AdmitResourceScopeReferenceRequest(
                        TenantId: context.TenantId,
                        ResourceGroupId: groupId,
                        IsolationScopeId: owned.IsolationScopeId,
                        Consumer: null),
                    session: session,
                    cancellationToken: token) is ResourceScopeReferenceUnavailable)
                return new(
                    operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ResourceScopeUnavailable));

            if (current is null)
            {
                var created = SecretFactory.CreateMetadata(operation.ResourceId,
                    context.TenantId,
                    groupId,
                    owned.IsolationScopeId,
                    operation.Declaration.Address.Name,
                    spec.Purpose,
                    spec.Keys,
                    clock.GetUtcNow().UtcDateTime);

                if (await registrar.RegisterAsync(
                        new RegisterResourceRequest(
                            TenantId: context.TenantId,
                            ResourceGroupId: groupId,
                            Resource: new(SecretResource.Type, created.Id, created.Name)),
                        session: session,
                        cancellationToken: token) is ResourceRegistrationMutationError)
                    return new(
                        created.Id,
                        new SecretDeclarationRejected(SecretDeclarationError.ResourceRegistrationUnavailable));

                await secrets.InsertOneAsync(session, created, cancellationToken: token);
                await publish(session, created, new SecretCreated(), token);

                return new(created.Id, Changed: true);
            }

            var transition = current.UpdateDeclaredMetadata(spec.Purpose, spec.Keys, clock.GetUtcNow().UtcDateTime);

            if (transition is SecretDeclaredKeysInUse)
                return new(current.Id, new SecretDeclarationRejected(SecretDeclarationError.SecretKeysInUse));

            var updated = (Secret)transition.Value;

            if (updated == current)
                return new(current.Id);

            await secrets.ReplaceOneAsync(
                session,
                SecretFilters.Identity(context.TenantId, current.Id),
                updated,
                cancellationToken: token);
            await publish(session, updated, new SecretMetadataUpdated(), token);

            return new(current.Id, Changed: true);
        },
        ct);

    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session, CancellationToken ct)
    {
        var current = await ReadCurrentAsync(new(request.TenantId, request.Declaration.Address), session, ct);
        if (current is DeclarationReadBlocked blocked)
            return blocked;
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var spec = DeclarationJson.Read<SecretDeclaration>(NormalizeAndValidate(request.Declaration.Spec));
        var groupAddress = new ResourceAddress("ResourceGroup", spec.ResourceGroup);
        var groupId = request.References.SingleOrDefault(item => item.Address == groupAddress)?.ResourceId;
        if (groupId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(groupAddress));

        string? scopeId = null;
        if (spec.IsolationScope is not null)
        {
            var scopeAddress = new ResourceAddress("IsolationScope", spec.IsolationScope);
            scopeId = request.References.SingleOrDefault(item => item.Address == scopeAddress)?.ResourceId;
            if (scopeId is null)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(scopeAddress));
        }

        if (await loadPlanningScope(new(request.TenantId, groupId, scopeId), session, ct)
            is not IsolationScopePlanningSnapshot scope)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(groupAddress));

        var desired = Project(groupId, scope.IsolationScopeId, spec.Purpose, spec.Keys) with
        { Dependencies = new(ScopeVersions(scope)) };
        if (!JsonElement.DeepEquals(found.Projection.Identity, desired.Identity))
            return new DeclarationReadBlocked(new DeclarationReplacementRequired());

        return new GitLifecycleAdoptionPreview(found, desired, request.References);
    }

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new GitLifecycleResourceAbsent();
        if (request.Resource.Address.Kind != Kind || current.Name != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        if (current.Management is ProviderManagedSecret)
            return new DeclarationReadBlocked(
                new SecretDeclarationRejected(SecretDeclarationError.ProviderManagedSecret));

        return new GitLifecycleResourceSnapshot(request.Resource, new(SecretPlanningFence.Versions(current)),
            GitLifecyclePresence.Active, GitLifecycleProtection.ProtectedData,
            GitLifecycleVersionSemantics.ObservedDocuments);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var read = await ReadLifecycleAsync(new(request.Authority.TenantId, request.Resource), session, ct);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();
        if (read is DeclarationReadBlocked blocked)
            return blocked;
        if (!SecretLifecycleVersions.Same(((GitLifecycleResourceSnapshot)read.Value).Version, request.Expected))
            return new DeclarationPreconditionsChanged();

        var result = await deletion.ExecuteAsync(new(request.Authority.TenantId, request.Resource.ResourceId,
            request.Expected, request.Authority), session, ct);
        return result switch
        {
            Unit or SecretMissing => new GitLifecycleResourceRemoved(),
            SecretBindingsRemain => new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
            SecretReferenceRejected => new DeclarationPreconditionsChanged(),
            SecretGitOwned => new StaleGitExecution()
        };
    }

    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await secrets.Find(session, SecretFilters.Identity(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        current ??= await secrets.Find(session, SecretFilters.TechnicalName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.Management is ProviderManagedSecret)
            return new DeclarationReadBlocked(
                new SecretDeclarationRejected(SecretDeclarationError.ProviderManagedSecret));
        if (request.Operation is { } accepted && current.Id == accepted.ResourceId &&
            current.Name != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());


        return new CurrentDeclaration(current.Id, new(SecretPlanningFence.Versions(current)),
            Project(current.ResourceGroupId, current.IsolationScopeId, current.Purpose, current.KeyNames),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (request.Operation is { } operation)
        {
            var acceptedGroup = await AcceptedScopeAsync(operation, session, cancellationToken);
            if (acceptedGroup is null)
                return new DeclarationReadBlocked(
                    new SecretDeclarationRejected(SecretDeclarationError.ResourceGroupUnavailable));

            var acceptedSpec = DeclarationJson.Read<SecretDeclaration>(request.Declaration.Spec);
            return Project(acceptedGroup.ResourceGroupId, acceptedGroup.IsolationScopeId,
                acceptedSpec.Purpose, acceptedSpec.Keys) with
            { Dependencies = new(ScopeVersions(acceptedGroup)) };
        }

        // Advisory planning can resolve names; execution above uses accepted IDs and transactional owner reads.
        var spec = DeclarationJson.Read<SecretDeclaration>(NormalizeAndValidate(request.Declaration.Spec));
        var address = new ResourceAddress("ResourceGroup", spec.ResourceGroup);
        var groupId = await git.ResolveAsync(new(request.TenantId, address), session, cancellationToken);
        if (groupId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

        string? scopeId = null;
        if (spec.IsolationScope is not null)
        {
            var scopeAddress = new ResourceAddress("IsolationScope", spec.IsolationScope);
            scopeId = await git.ResolveAsync(new(request.TenantId, scopeAddress), session, cancellationToken);
            if (scopeId is null)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(scopeAddress));
        }

        if (await loadPlanningScope(new(request.TenantId, groupId, scopeId), session, cancellationToken)
            is not IsolationScopePlanningSnapshot scope)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

        return Project(groupId, scope.IsolationScopeId, spec.Purpose, spec.Keys) with
        { Dependencies = new(ScopeVersions(scope)) };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
        IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var group = await AcceptedScopeAsync(request.Operation, session, cancellationToken);
        if (group is null || !ScopeVersions(group).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var current = await secrets.Find(session,
                SecretFilters.Identity(request.Operation.Context.TenantId, request.Operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null
                ? request.Expected.Documents.Any(item =>
                    item.DocumentId == SecretVersionKeys.Revision(request.Operation.ResourceId))
                : !SecretPlanningFence.Matches(current, request.Expected))
            return new DeclarationPreconditionsChanged();

        if (await fencePlanningScope(group, session, cancellationToken) is IsolationScopePlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (current is not null && !await planningFence.FenceAsync(current, session, cancellationToken))
            return new DeclarationPreconditionsChanged();

        return new DeclarationFenced();
    }

    private async Task<IsolationScopePlanningSnapshot?> AcceptedScopeAsync(GitOperationInput operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var spec = DeclarationJson.Read<SecretDeclaration>(operation.Declaration.Spec);
        var groupId = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
            new("ResourceGroup", spec.ResourceGroup)), session, ct);
        if (groupId is null)
            return null;

        string? scopeId = null;
        if (spec.IsolationScope is not null)
        {
            scopeId = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
                new("IsolationScope", spec.IsolationScope)), session, ct);
            if (scopeId is null)
                return null;
        }

        return await loadPlanningScope(new(operation.Context.TenantId, groupId, scopeId), session, ct)
            is IsolationScopePlanningSnapshot scope ? scope : null;
    }

    private static DeclarationDocumentVersion[] ScopeVersions(IsolationScopePlanningSnapshot scope) =>
    [
        new(ResourceGroupVersionKeys.Revision(scope.ResourceGroupId), scope.ResourceGroupRevision),
        new(ResourceGroupVersionKeys.IsolationScopeRevision(scope.IsolationScopeId), scope.ScopeRevision)
    ];

    private static DeclarationProjection Project(
        string resourceGroupId,
        string isolationScopeId,
        string purpose,
        IEnumerable<string> keys) => new(
            DeclarationJson.Write(new { resourceGroupId, isolationScopeId }),
            DeclarationJson.Write(new
            {
                purpose = purpose.Trim(),
                keys = keys.Order(StringComparer.Ordinal).ToArray()
            }));

    public async Task<ReadRegisteredResourceOwnershipOutcome> ReadRegistrationOwnershipAsync(
        ReadRegisteredResourceOwnershipRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());

        var version = new DeclarationVersionToken(SecretPlanningFence.Versions(current));
        if (current.Management is UnmanagedSecret)
            return new IndependentRegisteredResourceOwnership(version);
        if (current.Management is not ProviderManagedSecret owner || current.Bindings.Length != 1 ||
            string.IsNullOrWhiteSpace(owner.ResourceId))
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());

        var binding = current.Bindings[0];
        if (binding.Id != owner.BindingId ||
            binding.Consumer.ResourceType != owner.ResourceType || binding.Consumer.ResourceId != owner.ResourceId)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());

        ResourceTypeId ownerType;
        try
        {
            ownerType = ResourceTypeId.Create(owner.ResourceType);
        }
        catch (ArgumentException)
        {
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());
        }

        var registration = await loadRegistration(new(request.TenantId, ownerType, owner.ResourceId), session,
            cancellationToken);
        if (registration is null || registration.Resource.ResourceGroupId != current.ResourceGroupId)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());

        return new ManagedRegisteredResourceOwnership(ownerType, owner.ResourceId, version);
    }

    public async Task<FenceDeclarationOutcome> FenceRegistrationOwnershipAsync(
        FenceRegisteredResourceOwnershipRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Registration ownership fencing requires a transaction.");

        var ownership = await ReadRegistrationOwnershipAsync(new(request.TenantId, request.ResourceId), session,
            cancellationToken);
        if (ownership is DeclarationReadBlocked blocked)
            return blocked;

        var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.ResourceId))
            .SingleAsync(cancellationToken);
        return SecretPlanningFence.Matches(current, request.Expected) &&
               await planningFence.FenceAsync(current, session, cancellationToken)
            ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
    }

    public async Task<IReadOnlyList<string>> ReadLifecycleWarningsAsync(
        ReadLifecycleWarningsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Lifecycle warning reads require an active transaction.");

        if (request.Action is not (GitLifecycleAction.Remove or GitLifecycleAction.Replace) ||
            !await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.ResourceId))
                .AnyAsync(cancellationToken))
            return [];

        return request.Action == GitLifecycleAction.Replace
            ? ["SecretStoredVersionsDeleted", "ReplacementSecretValuesRequired"]
            : ["SecretStoredVersionsDeleted"];
    }
}

internal static class SecretLifecycleVersions
{
    public static bool Same(DeclarationVersionToken left, DeclarationVersionToken right) =>
        left.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}
