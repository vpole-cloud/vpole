using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.Backups.Application.Declarations;

internal sealed record BackupDeclarationRead(
    string TenantId,
    CompiledDeclaration Declaration,
    GitOperationInput? Operation,
    IReadOnlyList<GitPreviewResolvedReference>? Preview,
    IGitOpsMutations Git,
    IClientSessionHandle Session,
    CancellationToken CancellationToken
)
{
    public Task<string?> ResolveAsync(ResourceAddress address)
    {
        if (Preview is not null)
            return Task.FromResult(Preview.SingleOrDefault(item => item.Address == address)?.ResourceId);
        return Operation is null
            ? Git.ResolveAsync(new(TenantId, address), Session, CancellationToken)
            : Git.ResolveAcceptedAsync(
                new(Operation.Context, Operation.ResourceId, address),
                Session,
                CancellationToken);
    }
}

internal sealed record ResolvedBackupDeclaration<T>(
    T Input,
    DeclarationProjection Projection
);

internal sealed record BackupDeclarationRejected(
    string Reason
) : IDeclarationFailure;

internal abstract class BackupDeclarationProvider<T, TInput>(
    IMongoCollection<T> documents,
    IGitOpsMutations git
) : ITransactionalDeclarativeResourceProvider, IDeclarativeAdoptionPreviewProvider,
    IDeclarativeResourceLifecycleProvider
    where T : class, IBackupConfigurationDocument
{
    public abstract string Kind { get; }
    public abstract JsonElement NormalizeAndValidate(JsonElement spec);
    public abstract DeclarationFacts Facts(JsonElement spec);
    public abstract IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec);
    protected abstract DeclarationProjection Project(T entity);
    protected abstract Task<ResolvedBackupDeclaration<TInput>?> ResolveAsync(BackupDeclarationRead read, bool fence);

    protected abstract Task<BackupWriteResult<T>> SaveAsync(
        BackupConfigurationWrite<TInput> write,
        IClientSessionHandle session,
        CancellationToken ct);

    protected abstract Task<BackupWriteResult<bool>> DeleteAsync(
        string tenantId,
        string id,
        long revision,
        IClientSessionHandle session,
        CancellationToken ct);

    protected abstract UpdateDefinition<T> PlanningFence { get; }
    protected virtual bool Deleting(T entity) => false;
    protected virtual bool AsynchronousDeletion => false;
    protected abstract GitLifecycleProtection Protection { get; }

    private DeclarationDocumentVersion Version(T entity) => new(
        DeclarationVersionKeys.Resource(Kind, entity.Id),
        entity.Revision);

    private Task<T?> ByIdAsync(
        string tenant,
        string id,
        IClientSessionHandle session,
        CancellationToken ct) => documents.Find(session, BackupsFilters<T>.ById(tenant, id)).SingleOrDefaultAsync(ct)!;

    public string DescribeFailure(IDeclarationFailure failure) => failure is BackupDeclarationRejected rejected
        ? rejected.Reason
        : throw new ArgumentException("Unknown backup declaration failure.", nameof(failure));

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await documents.Find(session, BackupConfigurationFilters<T>.ByName(request.TenantId, request.Name))
            .SingleOrDefaultAsync(ct))?.Id;

    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = request.Operation is { } operation
            ? await ByIdAsync(request.TenantId, operation.ResourceId, session, ct)
            : null;
        current ??= await documents.Find(
                session,
                BackupConfigurationFilters<T>.ByName(request.TenantId, request.Address.Name))
            .SingleOrDefaultAsync(ct);
        if (current is null)
            return new CurrentDeclarationMissing();
        if (current.Name != request.Address.Name || Deleting(current))
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        return new CurrentDeclaration(
            current.Id,
            new([Version(current)]),
            Project(current),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var desired = await ResolveAsync(
            new(request.TenantId, request.Declaration, request.Operation, null, git, session, ct),
            false);
        if (desired is null)
            return Unavailable();
        return desired.Projection;
    }

    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = await ReadCurrentAsync(new(request.TenantId, request.Declaration.Address), session, ct);
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        var desired = await ResolveAsync(
            new(request.TenantId, request.Declaration, null, request.References, git, session, ct),
            false);
        if (desired is null)
            return Unavailable();
        return new GitLifecycleAdoptionPreview(found, desired.Projection, request.References);
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var op = request.Operation;
        var desired = await ResolveAsync(new(op.Context.TenantId, op.Declaration, op, null, git, session, ct), true);
        if (desired is null ||
            desired.Projection.Dependencies?.Documents.Any(v => !request.Expected.Documents.Contains(v)) == true)
            return new DeclarationPreconditionsChanged();
        var key = DeclarationVersionKeys.Resource(Kind, op.ResourceId);
        var expected = request.Expected.Documents.SingleOrDefault(item => item.DocumentId == key);
        if (expected is null)
        {
            if (await ByIdAsync(op.Context.TenantId, op.ResourceId, session, ct) is not null)
                return new DeclarationPreconditionsChanged();
        }
        else
        {
            var result = await documents.UpdateOneAsync(
                session,
                BackupConfigurationFilters<T>.AtRevision(op.Context.TenantId, op.ResourceId, expected.Revision),
                PlanningFence,
                cancellationToken: ct);
            if (result.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) => git.ExecuteAsync(
        operation,
        async (session, token) =>
        {
            var desired = await ResolveAsync(
                new(operation.Context.TenantId, operation.Declaration, operation.Input, null, git, session, token),
                false);
            if (desired is null)
                return new GitOperationResult(operation.ResourceId, Unavailable().Failure);
            var current = await ByIdAsync(operation.Context.TenantId, operation.ResourceId, session, token);
            var revision = current?.Revision ?? 0;
            var result = await SaveAsync(
                new(operation.Context.TenantId, operation.ResourceId, revision, desired.Input),
                session,
                token);
            return result switch
            {
                BackupWriteResult<T>.Saved saved => new GitOperationResult(
                    saved.Value.Id,
                    Changed: saved.Value.Revision != revision),
                BackupWriteResult<T>.Rejected rejected => new GitOperationResult(
                    operation.ResourceId,
                    new BackupDeclarationRejected(rejected.Code)),
            };
        },
        ct);

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var entity = await ByIdAsync(request.TenantId, request.Resource.ResourceId, session, ct);
        if (entity is null)
            return new GitLifecycleResourceAbsent();
        if (request.Resource.Address != new ResourceAddress(Kind, entity.Name))
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        return new GitLifecycleResourceSnapshot(
            request.Resource,
            new([Version(entity)]),
            Deleting(entity) ? GitLifecyclePresence.Deleting : GitLifecyclePresence.Active,
            Protection,
            GitLifecycleVersionSemantics.ManagedConfiguration);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = await ReadLifecycleAsync(new(request.Authority.TenantId, request.Resource), session, ct);
        if (current is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();
        if (current is DeclarationReadBlocked blocked)
            return blocked;
        var snapshot = (GitLifecycleResourceSnapshot)current.Value;
        var version = snapshot.Version.Documents.Single();
        if (!request.Expected.Documents.Contains(version))
            return new DeclarationPreconditionsChanged();
        if (!await git.AuthorizeAsync(
                new(
                    request.Authority.TenantId,
                    Kind,
                    request.Resource.ResourceId,
                    Lifecycle: request.Authority,
                    LifecycleEffect: GitLifecycleMutationEffect.Remove),
                session,
                ct))
            return new StaleGitExecution();
        var result = await DeleteAsync(
            request.Authority.TenantId,
            request.Resource.ResourceId,
            version.Revision,
            session,
            ct);
        if (result is BackupWriteResult<bool>.Rejected rejected)
            return new DeclarationReadBlocked(new BackupDeclarationRejected(rejected.Code));
        if (AsynchronousDeletion)
            return new GitLifecycleRemovalRequested();
        return new GitLifecycleResourceRemoved();
    }

    private static DeclarationReadBlocked Unavailable() =>
        new(new BackupDeclarationRejected("backup-declaration-reference-unavailable"));
}
