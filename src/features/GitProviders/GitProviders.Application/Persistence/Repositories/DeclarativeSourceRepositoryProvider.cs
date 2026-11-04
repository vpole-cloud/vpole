using System.Text.Json;
using System.Globalization;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Workloads;

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

internal sealed partial class DeclarativeSourceRepositoryProvider(
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
}
