using Pole.Features.SourceBuilds.Domain;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.SourceBuilds.Application.Common;

public sealed record SourceBuildRecipeDto(string Dockerfile)
{
    public SourceBuildRecipe ToDomain() => SourceBuildRecipe.Create(Dockerfile);
}

public sealed record SourceBuildConfigurationDto(
    string Id,
    string TechnicalName,
    long Revision,
    string SourceRepositoryId,
    string WebServiceId,
    SourceBuildRecipeDto Recipe,
    string? CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    GitResourceStatus? GitOps);

public sealed record SourceBuildJobStateDto(
    string Status,
    SourceBuildPhase? Phase,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? ImageReference,
    string? ImageDigest,
    string? ReleaseId,
    string? FailureCode,
    string? FailureMessage);

public sealed record SourceBuildJobDto(
    string Id,
    string ConfigurationId,
    string SourceRepositoryId,
    string WebServiceId,
    string Branch,
    string CommitSha,
    SourceBuildRecipeDto Recipe,
    SourceBuildJobStateDto State,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public static class SourceBuildDtoMappings
{
    public static SourceBuildConfigurationDto ToDto(
        this SourceBuildConfiguration configuration, GitResourceStatus? gitOps = null) =>
        new(
            configuration.Id,
            configuration.TechnicalName,
            configuration.Revision, configuration.SourceRepositoryId,
            configuration.WebServiceId,
            configuration.Recipe.ToDto(),
            (configuration.Origin as ManualSourceBuildConfigurationOrigin)?.UserId,
            configuration.CreatedAtUtc,
            configuration.UpdatedAtUtc,
            gitOps);

    public static SourceBuildRecipeDto ToDto(this SourceBuildRecipe recipe) => new(recipe.Dockerfile);

    public static SourceBuildJobDto ToDto(this SourceBuildJob job) =>
        new(
            job.Id,
            job.ConfigurationId,
            job.SourceRepositoryId,
            job.WebServiceId,
            job.Branch,
            job.CommitSha,
            job.Recipe.ToDto(),
            job.State.ToDto(job.CreatedAtUtc),
            job.CreatedAtUtc,
            job.UpdatedAtUtc);

    private static SourceBuildJobStateDto ToDto(
        this SourceBuildJobState state,
        DateTime createdAtUtc) =>
        state switch
        {
            QueuedSourceBuild queued => new(
                "queued", SourceBuildPhase.Queued, queued.QueuedAtUtc, null, null, null, null, null, null),
            CloningSourceBuild cloning => new(
                "cloning", SourceBuildPhase.Cloning, cloning.StartedAtUtc, null, null, null, null, null, null),
            AnalyzingSourceBuild analyzing => new(
                "analyzing", SourceBuildPhase.Analyzing, analyzing.StartedAtUtc, null, null, null, null, null, null),
            BuildingSourceBuild building => new(
                "building", SourceBuildPhase.Building, building.StartedAtUtc, null, null, null, null, null, null),
            PushingSourceBuild pushing => new(
                "pushing", SourceBuildPhase.Pushing, pushing.StartedAtUtc, null,
                pushing.Artifact.Reference, null, null, null, null),
            DeployingSourceBuild deploying => new(
                "deploying", SourceBuildPhase.Deploying, deploying.StartedAtUtc, null,
                deploying.Artifact.Reference, deploying.Artifact.Digest, deploying.ReleaseId, null, null),
            SucceededSourceBuild succeeded => new(
                "succeeded", null, createdAtUtc, succeeded.CompletedAtUtc, succeeded.Artifact.Reference,
                succeeded.Artifact.Digest, succeeded.ReleaseId, null, null),
            FailedSourceBuild failed => new(
                "failed", failed.Phase, createdAtUtc, failed.FailedAtUtc, null, null, null, failed.Code, failed.Message),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
}
