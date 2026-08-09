using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Common;

public sealed class SourceRepositoryBuildAccess(
    IGitProviderDbContext dbContext,
    IGitProviderRepositoryService repositoryService)
    : ISourceRepositoryBuildAccess
{
    public async Task<SourceRepositoryBuildSnapshot?> FindAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var repository = await dbContext.SourceRepositories
            .Find(item =>
                item.TenantId == tenantId &&
                item.Id == sourceRepositoryId)
            .FirstOrDefaultAsync(cancellationToken);
        return repository?.ToBuildSnapshot();
    }

    public async Task<Result> DownloadArchiveAsync(
        string tenantId,
        string sourceRepositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        var repository = await dbContext.SourceRepositories
            .Find(item =>
                item.TenantId == tenantId &&
                item.Id == sourceRepositoryId)
            .FirstOrDefaultAsync(cancellationToken);
        if (repository is null)
            return Failures.NotFound("Source repository not found.", sourceRepositoryId);

        var connection = await dbContext.GitProviderConnections
            .Find(item =>
                item.TenantId == tenantId &&
                item.Id == repository.ConnectionId)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null)
            return Failures.Conflict("The linked git provider connection no longer exists.");

        return await repositoryService.DownloadRepositoryArchiveAsync(
            connection,
            repository.ExternalRepositoryId,
            commitSha,
            destination,
            cancellationToken);
    }
}

internal static class SourceRepositoryBuildMappings
{
    public static SourceRepositoryBuildSnapshot ToBuildSnapshot(this SourceRepository repository) =>
        new(
            repository.Id,
            repository.CandidateKey,
            repository.Branch,
            repository.CommitSha,
            new SourceRepositoryBuildPlanSnapshot(
                repository.SelectedPlan.CandidateKey,
                repository.SelectedPlan.Kind switch
                {
                    SourceBuildPlanKind.Dockerfile => SourceRepositoryBuildPlanKind.Dockerfile,
                    SourceBuildPlanKind.DotNet => SourceRepositoryBuildPlanKind.DotNet,
                    SourceBuildPlanKind.Node => SourceRepositoryBuildPlanKind.Node,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(repository),
                        repository.SelectedPlan.Kind,
                        "Unsupported source build plan kind.")
                },
                repository.SelectedPlan.SourceRoot,
                repository.SelectedPlan.ManifestPath,
                repository.SelectedPlan.Commands.ToArray(),
                repository.SelectedPlan.StartCommand,
                repository.SelectedPlan.DockerfilePreview));
}

public static class GitProviderCapabilityRegistration
{
    public static IServiceCollection AddGitProviderCapabilities(this IServiceCollection services)
    {
        services.TryAddScoped<ISourceRepositoryBuildAccess, SourceRepositoryBuildAccess>();
        return services;
    }
}
