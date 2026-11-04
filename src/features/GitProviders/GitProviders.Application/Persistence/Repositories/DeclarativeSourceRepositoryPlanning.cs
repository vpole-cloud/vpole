using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Repositories;

internal sealed partial class DeclarativeSourceRepositoryProvider
{
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

        return new CurrentDeclaration(current.Id, new([new($"SourceRepository/{current.Id}", current.Revision)]),
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
            item.DocumentId == $"SourceRepository/{operation.ResourceId}");
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
        new($"GitProviderConnection/{connection.Id}", connection.ReferenceFence);
}
