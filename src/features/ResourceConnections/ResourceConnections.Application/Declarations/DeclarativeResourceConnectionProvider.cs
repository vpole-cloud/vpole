// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Declarations;

public sealed record ResourceConnectionDeclaration(
    ResourceAddress Source,
    ResourceAddress Destination,
    string Endpoint
);

public sealed record ConnectionDeclarationUnavailable : IDeclarationFailure;

public sealed record ConnectionDeclarationEndpointUnavailable : IDeclarationFailure;

public sealed record ConnectionDeclarationTopologyUnavailable : IDeclarationFailure;

internal sealed class DeclarativeResourceConnectionProvider(
    IMongoCollection<ResourceConnection> connections,
    IEnumerable<ResourceConnectionDeclarationKind> registeredKinds,
    ResourceConnectionEndpointCatalog endpoints,
    ResourceConnectionTopologyAdmission admission,
    MongoCreateResourceConnection creation,
    MongoBeginResourceConnectionDeletion deletion,
    LoadResourceRegistrationPlanningSnapshot loadRegistration,
    FenceResourceRegistrationPlanningSnapshot fenceRegistration,
    IGitOpsMutations git
) : ITransactionalDeclarativeResourceProvider, IDeclarativeResourceLifecycleProvider,
    IDeclarativeLifecycleDependencyProvider, IDeclarativeAdoptionPreviewProvider
{
    private const string DestinationField = "spec.destination";

    private const string SourceField = "spec.source";

    internal const string ResourceKind = "ResourceConnection";

    private readonly IReadOnlyDictionary<string, ResourceTypeId> kinds = registeredKinds.ToDictionary(
        item => item.Kind,
        item => item.ResourceType,
        StringComparer.Ordinal);

    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await connections
            .Find(session, ResourceConnectionFilters.ByTenantAndId(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null || current.Lifecycle is not ActiveResourceConnection)
            return new DeclarationReadBlocked(new ConnectionDeclarationUnavailable());

        if (request.Declaration.Address.Kind != Kind || current.Name != request.Declaration.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var spec = DeclarationJson.Read<ResourceConnectionDeclaration>(request.Declaration.Spec);
        var sourceId = ReferenceId(spec.Source);
        var destinationId = ReferenceId(spec.Destination);
        if (sourceId is null || destinationId is null || !kinds.TryGetValue(spec.Source.Kind, out var sourceType) ||
            !kinds.TryGetValue(spec.Destination.Kind, out var destinationType))
            return new DeclarationReadBlocked(
                new DeclarationReferenceUnavailable(sourceId is null ? spec.Source : spec.Destination));

        var command = new CreateResourceConnectionCommand(
            request.TenantId,
            request.Declaration.Address.Name,
            sourceType,
            sourceId,
            destinationType,
            destinationId,
            spec.Endpoint);
        var projection = Project(
            new CreateResourceConnectionCommand(
                current.TenantId,
                current.Name,
                ResourceTypeId.Create(current.SourceResourceTypeId),
                current.SourceResourceId,
                ResourceTypeId.Create(current.DestinationResourceTypeId),
                current.DestinationResourceId,
                current.EndpointId));
        var desiredProjection = Project(command);
        var currentDeclaration = new CurrentDeclaration(
            current.Id,
            LifecycleVersion(current),
            projection,
            DeclarationConfigurationState.Configured);
        var source = await loadRegistration(
            new LoadRegisteredResourceRequest(request.TenantId, sourceType, sourceId),
            session,
            cancellationToken);
        var destination = await loadRegistration(
            new LoadRegisteredResourceRequest(request.TenantId, destinationType, destinationId),
            session,
            cancellationToken);
        if (source is null || destination is null)
            // The engine proves reserved IDs against the complete reviewed snapshot. A replacement
            // can project those identities before their prerequisite resources exist; actual endpoint
            // and placement admission remains mandatory in the normal Bind phase.
            return !JsonElement.DeepEquals(projection.Identity, desiredProjection.Identity)
                ? new GitLifecycleAdoptionPreview(currentDeclaration, desiredProjection, [])
                : new DeclarationReadBlocked(
                    new DeclarationReferenceUnavailable(source is null ? spec.Source : spec.Destination));

        var resolved = new ResolvedDeclaration(
            command,
            new[] { source, destination }.DistinctBy(item => item.Resource.RegistrationId)
                .OrderBy(item => item.Resource.RegistrationId, StringComparer.Ordinal)
                .ToArray());
        var desired = await ProjectResolvedAsync(resolved, session, cancellationToken);
        return desired switch
        {
            DeclarationProjection accepted => new GitLifecycleAdoptionPreview(currentDeclaration, accepted, []),
            DeclarationReadBlocked blocked => blocked,
        };

        string? ReferenceId(ResourceAddress address)
        {
            var references = request.References.Where(item => item.Address == address).ToArray();
            return references.Length == 1 ? references[0].ResourceId : null;
        }
    }

    public string DependencyOwner => "ResourceConnections";

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!kinds.TryGetValue(request.Resource.Address.Kind, out var resourceType))
            return [];

        var affected = await connections
            .Find(
                session,
                ResourceConnectionFilters.ByResource(request.TenantId, resourceType, request.Resource.ResourceId))
            .SortBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return affected.Select(item => new GitLifecycleDependency(
                new GitLifecycleResourceReference(new ResourceAddress(Kind, item.Name), item.Id),
                request.Resource,
                LifecycleVersion(item),
                GitLifecycleDependencyOrder.AfterDependentRemovalRequested))
            .ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection dependency fencing requires a transaction.");

        var current = await ReadDependenciesAsync(
            new ReadLifecycleDependenciesRequest(request.TenantId, request.Resource),
            session,
            cancellationToken);
        if (current.Count != request.Expected.Count || current.Any(item => !request.Expected.Any(expected =>
                expected.Dependent == item.Dependent && expected.Target == item.Target &&
                expected.Order == item.Order && SameVersions(expected.Version, item.Version))))
            return new DeclarationPreconditionsChanged();

        if (!kinds.TryGetValue(request.Resource.Address.Kind, out var resourceType))
            return new DeclarationFenced();

        // Connection admission writes this same registration. This closes the phantom-reference race
        // even when the reviewed dependency set was empty.
        var registration = await loadRegistration(
            new LoadRegisteredResourceRequest(request.TenantId, resourceType, request.Resource.ResourceId),
            session,
            cancellationToken);
        if (registration is not null &&
            await fenceRegistration(registration, session, cancellationToken) is not ResourceRegistrationSnapshotFenced)
            return new DeclarationPreconditionsChanged();

        foreach (var dependency in current)
        {
            var connection = await connections.Find(
                    session,
                    ResourceConnectionFilters.ByTenantAndId(request.TenantId, dependency.Dependent.ResourceId))
                .SingleAsync(cancellationToken);
            if (!SameVersions(LifecycleVersion(connection), dependency.Version))
                return new DeclarationPreconditionsChanged();
            // Deleting configuration is frozen. The parent's mandatory joined reviewed cascade
            // conditionally writes this exact live token after authorization in the same transaction.
            // Advancing it here would invalidate the approved cascade's own preconditions.
            if (connection.Lifecycle is DeletingResourceConnection)
                continue;

            var revision = dependency.Version.Documents[0].Revision;
            var fence = dependency.Version.Documents[1].Revision;
            var result = await connections.UpdateOneAsync(
                session,
                ResourceConnectionFilters.AtPlanningVersion(
                    request.TenantId,
                    new ResourceConnectionPlanningVersion(dependency.Dependent.ResourceId, revision, fence)),
                Builders<ResourceConnection>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            if (result.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await connections.Find(
                session,
                ResourceConnectionFilters.ByTenantAndId(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (request.Resource.Address.Kind != Kind || current.Name != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        return new GitLifecycleResourceSnapshot(
            request.Resource,
            LifecycleVersion(current),
            current.Lifecycle is ActiveResourceConnection ? GitLifecyclePresence.Active : GitLifecyclePresence.Deleting,
            GitLifecycleProtection.ConfigurationOnly,
            GitLifecycleVersionSemantics.ObservedDocuments);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var read = await ReadLifecycleAsync(
            new ReadLifecycleResourceRequest(request.Authority.TenantId, request.Resource),
            session,
            cancellationToken);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var current = (GitLifecycleResourceSnapshot)read.Value;
        if (!SameVersions(current.Version, request.Expected))
            return new DeclarationPreconditionsChanged();

        var expected = ResourceConnectionPlanningVersion.FromDocuments(
            request.Resource.ResourceId,
            current.Version.Documents.ToDictionary(item => item.DocumentId, item => item.Revision))!;
        var outcome = await deletion.ExecuteAsync(
            new BeginResourceConnectionDeletionRequest(
                request.Authority.TenantId,
                request.Resource.ResourceId,
                expected,
                request.Authority),
            session,
            cancellationToken);
        return outcome switch
        {
            ResourceConnection => new GitLifecycleRemovalRequested(),
            ResourceConnectionMissing => new GitLifecycleResourceRemoved(),
            ResourceConnectionDeletionConflict => new DeclarationPreconditionsChanged(),
            ResourceConnectionGitOwned => new StaleGitExecution(),
        };
    }

    public string Kind => ResourceKind;

    public int MaximumNameLength => 30;

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        ConnectionDeclarationUnavailable => nameof(ConnectionDeclarationUnavailable),
        ConnectionDeclarationEndpointUnavailable => nameof(ConnectionDeclarationEndpointUnavailable),
        ConnectionDeclarationTopologyUnavailable => nameof(ConnectionDeclarationTopologyUnavailable),
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure)),
    };

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<ResourceConnectionDeclaration>(spec);
        DeclarationJson.Require(
            declaration.Source is not null && DeclarationNames.Valid(declaration.Source.Name) &&
            kinds.TryGetValue(declaration.Source.Kind, out var source) && endpoints.SupportsSource(source),
            SourceField);
        DeclarationJson.Require(
            declaration.Destination is not null && DeclarationNames.Valid(declaration.Destination.Name) &&
            kinds.TryGetValue(declaration.Destination.Kind, out var destination) &&
            endpoints.SupportsDestination(destination),
            DestinationField);
        DeclarationJson.Require(
            !string.IsNullOrWhiteSpace(declaration.Endpoint) && declaration.Endpoint.Length <= 100 &&
            !declaration.Endpoint.Any(char.IsControl),
            "spec.endpoint");
        return DeclarationJson.Write(declaration);
    }

    public DeclarationFacts Facts(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<ResourceConnectionDeclaration>(spec);
        return new DeclarationFacts(
            null,
            [],
            [
                new DeclaredReference(declaration.Source, SourceField),
                new DeclaredReference(declaration.Destination, DestinationField),
            ],
            [],
            [],
            ReferencesShareScope: false);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var declaration = DeclarationJson.Read<ResourceConnectionDeclaration>(spec);
        return
        [
            new DeclarationStep(
                DeclarationPhase.Bind,
                [
                    new DeclarationDependency(declaration.Source, DeclarationPhase.Prepare),
                    new DeclarationDependency(declaration.Destination, DeclarationPhase.Configure),
                ]),
        ];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) => (await connections
        .Find(session, ResourceConnectionFilters.ByName(request.TenantId, request.Name))
        .FirstOrDefaultAsync(cancellationToken))?.Id;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken cancellationToken) =>
        git.ExecuteAsync(
            operation,
            async (session, ct) =>
            {
                var resolved = await ResolveDeclarationAsync(
                    operation.Context.TenantId,
                    operation.Declaration,
                    operation.Input,
                    session,
                    ct);
                if (resolved is null)
                    return new GitOperationResult(operation.ResourceId, new ConnectionDeclarationUnavailable());

                var result = await creation.CreateAsync(
                    new CreateResourceConnectionInSession(operation.ResourceId, resolved.Command),
                    session,
                    ct);
                return result switch
                {
                    ResourceConnection => new GitOperationResult(operation.ResourceId, Changed: true),
                    ConnectionNameAlreadyExists => new GitOperationResult(
                        operation.ResourceId,
                        new DeclarationPreconditionsChanged()),
                    ConnectionResourceMissing => new GitOperationResult(
                        operation.ResourceId,
                        new ConnectionDeclarationUnavailable()),
                    ConnectionPlacementDeleting => new GitOperationResult(
                        operation.ResourceId,
                        new ConnectionDeclarationUnavailable()),
                    ConnectionEndpointUnsupported => new GitOperationResult(
                        operation.ResourceId,
                        new ConnectionDeclarationEndpointUnavailable()),
                    ConnectionSourceHasNoPlacements => new GitOperationResult(
                        operation.ResourceId,
                        new ConnectionDeclarationTopologyUnavailable()),
                    ConnectionDestinationTargetMissing => new GitOperationResult(
                        operation.ResourceId,
                        new ConnectionDeclarationTopologyUnavailable()),
                    ConnectionDestinationTargetAmbiguous => new GitOperationResult(
                        operation.ResourceId,
                        new ConnectionDeclarationTopologyUnavailable()),
                };
            },
            cancellationToken);

    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await connections.Find(
                    session,
                    ResourceConnectionFilters.ByTenantAndId(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        current ??= await connections
            .Find(session, ResourceConnectionFilters.ByName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.Name != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        if (current.Lifecycle is not ActiveResourceConnection)
            return new DeclarationReadBlocked(new ConnectionDeclarationUnavailable());

        return new CurrentDeclaration(
            current.Id,
            new DeclarationVersionToken(
            [
                new DeclarationDocumentVersion(
                    ResourceConnectionPlanningVersion.RevisionDocumentId(current.Id),
                    current.Revision),
            ]),
            Project(
                new CreateResourceConnectionCommand(
                    current.TenantId,
                    current.Name,
                    ResourceTypeId.Create(current.SourceResourceTypeId),
                    current.SourceResourceId,
                    ResourceTypeId.Create(current.DestinationResourceTypeId),
                    current.DestinationResourceId,
                    current.EndpointId)),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveDeclarationAsync(
            request.TenantId,
            request.Declaration,
            request.Operation,
            session,
            cancellationToken);
        return resolved is null
            ? new DeclarationReadBlocked(new ConnectionDeclarationUnavailable())
            : await ProjectResolvedAsync(resolved, session, cancellationToken);
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var resolved = await ResolveDeclarationAsync(
            operation.Context.TenantId,
            operation.Declaration,
            operation,
            session,
            cancellationToken);
        if (resolved is null || !resolved.Versions.All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == ResourceConnectionPlanningVersion.RevisionDocumentId(operation.ResourceId));
        var current = await connections.Find(
                session,
                ResourceConnectionFilters.ByTenantAndId(operation.Context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null
                ? expected is not null
                : expected?.Revision != current.Revision || current.Lifecycle is not ActiveResourceConnection)
            return new DeclarationPreconditionsChanged();

        foreach (var registration in resolved.Registrations)
            if (await fenceRegistration(registration, session, cancellationToken) is not
                ResourceRegistrationSnapshotFenced)
                return new DeclarationPreconditionsChanged();

        if (current is not null)
        {
            var fenced = await connections.UpdateOneAsync(
                session,
                ResourceConnectionFilters.AtRevision(current.TenantId, current.Id, current.Revision),
                Builders<ResourceConnection>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    private static DeclarationProjection Project(CreateResourceConnectionCommand command) => new(
        DeclarationJson.Write(
            new
            {
                sourceType = command.SourceResourceType.Value,
                sourceId = command.SourceResourceId,
                destinationType = command.DestinationResourceType.Value,
                destinationId = command.DestinationResourceId,
                endpoint = command.EndpointId,
            }),
        DeclarationJson.Write(new { }));

    private async Task<ResolvedDeclaration?> ResolveDeclarationAsync(
        string tenantId,
        CompiledDeclaration declaration,
        GitOperationInput? operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<ResourceConnectionDeclaration>(declaration.Spec);
        var sourceId = await ResolveReferenceAsync(spec.Source);
        var destinationId = await ResolveReferenceAsync(spec.Destination);
        if (sourceId is null || destinationId is null)
            return null;

        var source = await loadRegistration(
            new LoadRegisteredResourceRequest(tenantId, kinds[spec.Source.Kind], sourceId),
            session,
            cancellationToken);
        var destination = await loadRegistration(
            new LoadRegisteredResourceRequest(tenantId, kinds[spec.Destination.Kind], destinationId),
            session,
            cancellationToken);
        return source is null || destination is null
            ? null
            : new ResolvedDeclaration(
                new CreateResourceConnectionCommand(
                    tenantId,
                    declaration.Address.Name,
                    kinds[spec.Source.Kind],
                    sourceId,
                    kinds[spec.Destination.Kind],
                    destinationId,
                    spec.Endpoint),
                new[] { source, destination }.DistinctBy(item => item.Resource.RegistrationId)
                    .OrderBy(item => item.Resource.RegistrationId, StringComparer.Ordinal)
                    .ToArray());

        Task<string?> ResolveReferenceAsync(ResourceAddress address) => operation is null
            ? git.ResolveAsync(new ResolveGitResourceRequest(tenantId, address), session, cancellationToken)
            : git.ResolveAcceptedAsync(
                new ResolveAcceptedGitReferenceRequest(operation.Context, operation.ResourceId, address),
                session,
                cancellationToken);
    }

    private static DeclarationVersionToken LifecycleVersion(ResourceConnection current) => new(
    [
        new DeclarationDocumentVersion(
            ResourceConnectionPlanningVersion.RevisionDocumentId(current.Id),
            current.Revision),
        new DeclarationDocumentVersion(
            ResourceConnectionPlanningVersion.ReferenceFenceDocumentId(current.Id),
            current.ReferenceFence),
    ]);

    private static bool SameVersions(DeclarationVersionToken left, DeclarationVersionToken right) => left.Documents
        .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
        .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));

    private async Task<ProjectDesiredDeclarationOutcome> ProjectResolvedAsync(
        ResolvedDeclaration resolved,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var topology = await admission.ReadAsync(resolved.Command, session, cancellationToken);
        return topology switch
        {
            ResolvedResourceConnectionTopology => Project(resolved.Command) with
            {
                Dependencies = new DeclarationVersionToken(resolved.Versions),
            },
            ConnectionResourceMissing => new DeclarationReadBlocked(new ConnectionDeclarationUnavailable()),
            ConnectionPlacementDeleting => new DeclarationReadBlocked(new ConnectionDeclarationUnavailable()),
            ConnectionEndpointUnsupported => new DeclarationReadBlocked(new ConnectionDeclarationEndpointUnavailable()),
            ConnectionSourceHasNoPlacements => new DeclarationReadBlocked(
                new ConnectionDeclarationTopologyUnavailable()),
            ConnectionDestinationTargetMissing => new DeclarationReadBlocked(
                new ConnectionDeclarationTopologyUnavailable()),
            ConnectionDestinationTargetAmbiguous => new DeclarationReadBlocked(
                new ConnectionDeclarationTopologyUnavailable()),
        };
    }

    private sealed record ResolvedDeclaration(
        CreateResourceConnectionCommand Command,
        IReadOnlyList<ResourceRegistrationPlanningSnapshot> Registrations
    )
    {
        public DeclarationDocumentVersion[] Versions =>
            Registrations.Select(item => new DeclarationDocumentVersion(
                    ResourceGroupVersionKeys.RegistrationReferenceFence(item.Resource.RegistrationId),
                    item.ReferenceFence))
                .ToArray();
    }
}
