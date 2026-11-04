using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Workloads;
using System.Globalization;
using System.Text.Json;

namespace Pole.Features.GitProviders.Application.Persistence.Repositories;

public sealed record SourceRepositoryDeclaration(
    string Connection,
    string RepositoryId,
    string Branch);

public sealed record SourceRepositoryDeclarationConnectionUnavailable : IDeclarationFailure;

public sealed record SourceRepositoryDeclarationRemoteMissing : IDeclarationFailure;

public sealed record SourceRepositoryDeclarationBranchMissing : IDeclarationFailure;

public sealed record SourceRepositoryDeclarationAccessDenied : IDeclarationFailure;

public sealed record SourceRepositoryDeclarationUnavailable : IDeclarationFailure;

public sealed record SourceRepositoryDeclarationLocationConflict : IDeclarationFailure;

internal sealed class DeclarativeSourceRepositoryProvider(
    IMongoCollection<SourceRepository> repositories,
    IMongoCollection<GitProviderConnection> connections,
    FindNamedGitProviderConnection findConnection,
    ResolveProviderRepository resolveRepository,
    MongoPersistLinkedSourceRepository creation,
    MongoRunTransaction transactions,
    IGitOpsMutations git,
    TimeProvider clock,
    MongoDeleteSourceRepository deletion) : ITransactionalDeclarativeResourceProvider, IDeclarativeReferencePreparation,
        IDeclarativeResourceLifecycleProvider, IDeclarativeAdoptionPreviewProvider, IDeclarativePreviewReferenceProvider
{
    public string Kind => "SourceRepository";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        SourceRepositoryDeclarationConnectionUnavailable => nameof(SourceRepositoryDeclarationConnectionUnavailable),
        SourceRepositoryDeclarationRemoteMissing => nameof(SourceRepositoryDeclarationRemoteMissing),
        SourceRepositoryDeclarationBranchMissing => nameof(SourceRepositoryDeclarationBranchMissing),
        SourceRepositoryDeclarationAccessDenied => nameof(SourceRepositoryDeclarationAccessDenied),
        SourceRepositoryDeclarationUnavailable => nameof(SourceRepositoryDeclarationUnavailable),
        SourceRepositoryDeclarationLocationConflict => nameof(SourceRepositoryDeclarationLocationConflict),
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<SourceRepositoryDeclaration>(spec);
        DeclarationJson.Require(ResourceTechnicalName.IsValid(declaration.Connection), "spec.connection");
        DeclarationJson.Require(ulong.TryParse(declaration.RepositoryId, NumberStyles.None,
            CultureInfo.InvariantCulture, out var repositoryId) && repositoryId > 0, "spec.repositoryId");
        DeclarationJson.Require(declaration.Branch is { Length: > 0 and <= 255 } &&
            !declaration.Branch.Any(char.IsControl) && !declaration.Branch.Any(char.IsWhiteSpace), "spec.branch");
        return DeclarationJson.Write(declaration with
        { RepositoryId = repositoryId.ToString(CultureInfo.InvariantCulture) });
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) =>
        [new(DeclarationPhase.Prepare, [])];

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        (await repositories.Find(session,
                GitProviderFilters.SourceRepositoryByTenantAndName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(cancellationToken))?.Id;

    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken cancellationToken)
    {
        var declaration = DeclarationJson.Read<SourceRepositoryDeclaration>(operation.Declaration.Spec);
        var connectionId = (await AcceptedConnectionIdAsync(operation, cancellationToken)).Id;
        if (connectionId is null)
        {
            var found = await findConnection(
                new(operation.Context.TenantId, declaration.Connection), cancellationToken);
            if (found is not NamedGitProviderConnectionFound connection)
                return new DeclarationReadBlocked(new SourceRepositoryDeclarationConnectionUnavailable());

            var pinned = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
                new(ConnectionAddress(declaration), connection.ConnectionId)), cancellationToken);
            if (pinned is StaleGitExecution stale)
                return stale;

            connectionId = ((GitReferencePinned)pinned.Value).ResourceId;
        }

        var resolved = await resolveRepository(new(operation.Context.TenantId, connectionId,
            declaration.RepositoryId, declaration.Branch), cancellationToken);
        return resolved is ResolvedProviderRepository
            ? new DeclarationReferencesPrepared()
            : new DeclarationReadBlocked(AccessFailure(resolved));
    }

    public async Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken cancellationToken)
    {
        var declaration = DeclarationJson.Read<SourceRepositoryDeclaration>(operation.Declaration.Spec);
        var connectionId = (await AcceptedConnectionIdAsync(operation.Input, cancellationToken)).Id;
        if (connectionId is null)
            return new(operation.ResourceId, new SourceRepositoryDeclarationConnectionUnavailable());

        // Remote access is checked outside the Mongo transaction. The pinned local identity is fenced inside it.
        var resolved = await resolveRepository(new(operation.Context.TenantId, connectionId,
            declaration.RepositoryId, declaration.Branch), cancellationToken);
        if (resolved is not ResolvedProviderRepository remote)
            return new(operation.ResourceId, AccessFailure(resolved));

        return await git.ExecuteAsync(operation, async (session, ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var repository = SourceRepositoryFactory.Create(operation.Context.TenantId,
                operation.Declaration.Address.Name, connectionId, declaration.RepositoryId,
                remote.Repository.FullName, remote.Repository.HtmlUrl, remote.Repository.IsPrivate, declaration.Branch,
                new(operation.ResourceId, now, now));
            var result = await creation.CreateAsync(repository, session, ct);
            return result switch
            {
                SourceRepository => new(operation.ResourceId, Changed: true),
                SourceRepositoryNameConflict => new(operation.ResourceId, new DeclarationPreconditionsChanged()),
                SourceRepositoryAlreadyLinked =>
                    new(operation.ResourceId, new SourceRepositoryDeclarationLocationConflict()),
                GitProviderConnectionNotFound => new(operation.ResourceId, new DeclarationPreconditionsChanged())
            };
        }, cancellationToken);
    }

    private Task<ConnectionReference> AcceptedConnectionIdAsync(
        GitOperationInput operation,
        CancellationToken cancellationToken) =>
        transactions.InvokeOutcomeAsync<ConnectionReference>(async (transaction, ct) =>
        {
            var declaration = DeclarationJson.Read<SourceRepositoryDeclaration>(operation.Declaration.Spec);
            var connection = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
                ConnectionAddress(declaration)), transaction.Session, ct);
            return transaction.Commit(new ConnectionReference(connection));
        }, cancellationToken);

    private sealed record ConnectionReference(string? Id);

    private static ResourceAddress ConnectionAddress(SourceRepositoryDeclaration declaration) =>
        new("GitProviderConnection", declaration.Connection);

    private static IDeclarationFailure AccessFailure(ResolveProviderRepositoryOutcome result) => result switch
    {
        GitProviderConnectionNotFound => new SourceRepositoryDeclarationConnectionUnavailable(),
        SourceRepositoryRemoteNotFound => new SourceRepositoryDeclarationRemoteMissing(),
        SourceRepositoryBranchNotFound => new SourceRepositoryDeclarationBranchMissing(),
        SourceRepositoryAccessDenied => new SourceRepositoryDeclarationAccessDenied(),
        SourceRepositoryUnavailable => new SourceRepositoryDeclarationUnavailable(),
        ResolvedProviderRepository => throw new InvalidOperationException("Successful access has no failure.")
    };

    private static DeclarationProjection Project(string connectionId, string repositoryId, string branch) =>
        new(DeclarationJson.Write(new { connectionId, repositoryId, branch }), DeclarationJson.Write(new { }));

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await repositories.Find(session,
            GitProviderFilters.SourceRepositoryByTenantAndId(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (request.Resource.Address.Kind != Kind || current.TechnicalName != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        return new GitLifecycleResourceSnapshot(request.Resource, LifecycleVersions(current),
            GitLifecyclePresence.Active, GitLifecycleProtection.ConfigurationOnly,
            GitLifecycleVersionSemantics.ObservedDocuments);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var read = await ReadLifecycleAsync(new(request.Authority.TenantId, request.Resource),
            session, cancellationToken);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();

        if (read is DeclarationReadBlocked blocked)
            return blocked;
        if (!SameVersions(((GitLifecycleResourceSnapshot)read.Value).Version, request.Expected))
            return new DeclarationPreconditionsChanged();

        var outcome = await deletion.ExecuteAsync(new(request.Authority.TenantId, request.Resource.ResourceId,
            request.Authority), session, cancellationToken);
        return outcome switch
        {
            SourceRepositoryDeleted or SourceRepositoryNotFound => new GitLifecycleResourceRemoved(),
            SourceRepositoryHasBuildConfiguration => new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
            SourceRepositoryGitOwned => new StaleGitExecution()
        };
    }

    public async Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<SourceRepositoryDeclaration>(request.Consumer.Spec);
        var address = ConnectionAddress(spec);
        var accepted = request.References.SingleOrDefault(item => item.Address == address);
        var filter = accepted is null
            ? GitProviderFilters.ConnectionByTenantAndName(request.TenantId, spec.Connection)
            : GitProviderFilters.ConnectionByTenantAndId(request.TenantId, accepted.ResourceId);
        var connection = await connections.Find(session, filter).FirstOrDefaultAsync(cancellationToken);
        if (connection is not { Provider: GitProviderKind.GitHub } || connection.TechnicalName != spec.Connection)
            return new DeclarationReadBlocked(new SourceRepositoryDeclarationConnectionUnavailable());
        return new GitPreviewReferencesSnapshot(request.References.Where(item => item.Address != address)
            .Append(new(address, connection.Id, new([ConnectionVersion(connection)]))).ToArray());
    }

    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(new(request.TenantId, request.Declaration.Address), session,
            cancellationToken);
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var read = await ReadPreviewReferencesAsync(new(request.TenantId, request.Declaration, request.References),
            session, cancellationToken);
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var references = ((GitPreviewReferencesSnapshot)read.Value).References;
        var spec = DeclarationJson.Read<SourceRepositoryDeclaration>(request.Declaration.Spec);
        var connection = references.Single(item => item.Address == ConnectionAddress(spec));
        return new GitLifecycleAdoptionPreview(found,
            Project(connection.ResourceId, spec.RepositoryId, spec.Branch) with
            { Dependencies = connection.Version }, references);
    }

    private static DeclarationVersionToken LifecycleVersions(SourceRepository repository) => new(
        [new(DeclarationVersionKeys.SourceRepository(repository.Id), repository.Revision),
            new(DeclarationVersionKeys.SourceRepositoryFence(repository.Id), repository.ReferenceFence)]);

    private static bool SameVersions(DeclarationVersionToken left, DeclarationVersionToken right) =>
        left.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));

    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await repositories.Find(session,
                    GitProviderFilters.SourceRepositoryByTenantAndId(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        current ??= await repositories.Find(session,
                GitProviderFilters.SourceRepositoryByTenantAndName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.TechnicalName != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        return new CurrentDeclaration(current.Id,
            new([new(DeclarationVersionKeys.SourceRepository(current.Id), current.Revision)]),
            Project(current.ConnectionId, current.ExternalRepositoryId, current.Branch),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var declaration = DeclarationJson.Read<SourceRepositoryDeclaration>(request.Declaration.Spec);
        var connection = await ConnectionAsync(request.TenantId, declaration, request.Operation, session,
            cancellationToken);
        return connection is null
            ? new DeclarationReadBlocked(new SourceRepositoryDeclarationConnectionUnavailable())
            : Project(connection.Id, declaration.RepositoryId, declaration.Branch) with
            { Dependencies = new([ConnectionVersion(connection)]) };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var declaration = DeclarationJson.Read<SourceRepositoryDeclaration>(operation.Declaration.Spec);
        var connection = await ConnectionAsync(operation.Context.TenantId, declaration, operation, session,
            cancellationToken);
        if (connection is null || !request.Expected.Documents.Contains(ConnectionVersion(connection)))
            return new DeclarationPreconditionsChanged();

        var current = await repositories.Find(session,
                GitProviderFilters.SourceRepositoryByTenantAndId(operation.Context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == DeclarationVersionKeys.SourceRepository(operation.ResourceId));
        if (current is null ? expected is not null : expected?.Revision != current.Revision)
            return new DeclarationPreconditionsChanged();

        var connectionFence = await connections.UpdateOneAsync(session,
            GitProviderFilters.ConnectionAtFence(connection.TenantId, connection.Id, connection.ReferenceFence),
            Builders<GitProviderConnection>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        if (connectionFence.ModifiedCount != 1)
            return new DeclarationPreconditionsChanged();

        if (current is not null)
        {
            var rootFence = await repositories.UpdateOneAsync(session,
                GitProviderFilters.SourceRepositoryAtRevision(current.TenantId, current.Id, current.Revision),
                Builders<SourceRepository>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            if (rootFence.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    private async Task<GitProviderConnection?> ConnectionAsync(
        string tenantId,
        SourceRepositoryDeclaration declaration,
        GitOperationInput? operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var connectionId = operation is null ? null : await git.ResolveAcceptedAsync(
            new(operation.Context, operation.ResourceId, ConnectionAddress(declaration)), session, cancellationToken);
        if (operation is not null && connectionId is null)
            return null;

        var filter = connectionId is null
            ? GitProviderFilters.ConnectionByTenantAndName(tenantId, declaration.Connection)
            : GitProviderFilters.ConnectionByTenantAndId(tenantId, connectionId);
        var connection = await connections.Find(session, filter).FirstOrDefaultAsync(cancellationToken);
        return connection is { Provider: GitProviderKind.GitHub } && connection.TechnicalName == declaration.Connection
            ? connection
            : null;
    }

    private static DeclarationDocumentVersion ConnectionVersion(GitProviderConnection connection) =>
        new(DeclarationVersionKeys.GitProviderConnection(connection.Id), connection.ReferenceFence);
}
