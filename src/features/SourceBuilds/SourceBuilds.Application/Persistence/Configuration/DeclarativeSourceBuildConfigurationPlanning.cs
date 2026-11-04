using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

internal sealed partial class DeclarativeSourceBuildConfigurationProvider
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await configurations.Find(session,
                SourceBuildConfigurationFilters.ById(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken) : null;
        current ??= await configurations.Find(session,
            SourceBuildConfigurationFilters.ByName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.TechnicalName != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        return new CurrentDeclaration(current.Id,
            new([new($"SourceBuildConfiguration/{current.Id}", current.Revision)]),
            Project(current.SourceRepositoryId, current.WebServiceId, current.Recipe.Dockerfile),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var references = await ReferencesAsync(request.TenantId, request.Declaration, request.Operation,
            session, cancellationToken);
        if (references is null)
            return new DeclarationReadBlocked(new SourceBuildConfigurationAssociationMismatch());

        var declaration = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(request.Declaration.Spec);
        return Project(references.Repository.Id, references.Target.WebServiceId, declaration.Recipe.Dockerfile)
            with
        {
            Dependencies = new(Versions(references))
        };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var references = await ReferencesAsync(operation.Context.TenantId, operation.Declaration, operation,
            session, cancellationToken);
        if (references is null || !Versions(references).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var current = await configurations.Find(session,
            SourceBuildConfigurationFilters.ById(operation.Context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == $"SourceBuildConfiguration/{operation.ResourceId}");
        if (current is null ? expected is not null : expected?.Revision != current.Revision)
            return new DeclarationPreconditionsChanged();

        if (await fenceRepository(new(operation.Context.TenantId, references.Repository.Id,
            references.Repository.Revision), session, cancellationToken) is not SourceRepositoryFenced)
            return new DeclarationPreconditionsChanged();

        if (await admitTarget(new(operation.Context.TenantId, references.Target.WebServiceId, operation.ResourceId,
            SourceBuildAdmissionPurpose.Configure), session, cancellationToken) is not SourceBuildWebServiceAdmitted)
            return new DeclarationPreconditionsChanged();

        if (current is not null)
        {
            var fenced = await configurations.UpdateOneAsync(session,
                SourceBuildConfigurationFilters.AtRevision(current),
                Builders<SourceBuildConfiguration>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }
}
