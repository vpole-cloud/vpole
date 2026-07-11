using System.Collections.Immutable;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Common;

public sealed record SourceBuildRecipeDto(
    string CandidateKey,
    SourceBuildRecipeKind Kind,
    string SourceRoot,
    string ManifestPath,
    ImmutableList<string> Commands,
    string? StartCommand,
    string Dockerfile,
    bool IsCustomized)
{
    public SourceBuildRecipe ToDomain(bool isCustomized) =>
        new(
            CandidateKey,
            Kind,
            SourceRoot,
            ManifestPath,
            Commands,
            StartCommand,
            Dockerfile,
            isCustomized);
}

public sealed record SourceBuildConfigurationDto(
    string Id,
    string SourceRepositoryId,
    string WebServiceId,
    SourceBuildRecipeDto Recipe,
    string CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

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
    public static SourceBuildRecipe ToBuildRecipe(this SourceBuildPlan plan) =>
        new(
            plan.CandidateKey,
            plan.Kind switch
            {
                SourceBuildPlanKind.Dockerfile => SourceBuildRecipeKind.Dockerfile,
                SourceBuildPlanKind.DotNet => SourceBuildRecipeKind.DotNet,
                SourceBuildPlanKind.Node => SourceBuildRecipeKind.Node,
                _ => throw new ArgumentOutOfRangeException(nameof(plan.Kind), plan.Kind, null)
            },
            plan.SourceRoot,
            plan.ManifestPath,
            plan.Commands,
            plan.StartCommand,
            plan.DockerfilePreview,
            IsCustomized: false);

    public static SourceBuildConfigurationDto ToDto(this SourceBuildConfiguration configuration) =>
        new(
            configuration.Id,
            configuration.SourceRepositoryId,
            configuration.WebServiceId,
            configuration.Recipe.ToDto(),
            configuration.CreatedByUserId,
            configuration.CreatedAtUtc,
            configuration.UpdatedAtUtc);

    public static SourceBuildRecipeDto ToDto(this SourceBuildRecipe recipe) =>
        new(
            recipe.CandidateKey,
            recipe.Kind,
            recipe.SourceRoot,
            recipe.ManifestPath,
            recipe.Commands,
            recipe.StartCommand,
            recipe.Dockerfile,
            recipe.IsCustomized);

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
                "pushing", SourceBuildPhase.Pushing, pushing.StartedAtUtc, null, pushing.ImageReference, null, null, null, null),
            DeployingSourceBuild deploying => new(
                "deploying", SourceBuildPhase.Deploying, deploying.StartedAtUtc, null, deploying.ImageReference,
                deploying.ImageDigest, deploying.ReleaseId, null, null),
            SucceededSourceBuild succeeded => new(
                "succeeded", null, createdAtUtc, succeeded.CompletedAtUtc, succeeded.ImageReference,
                succeeded.ImageDigest, succeeded.ReleaseId, null, null),
            FailedSourceBuild failed => new(
                "failed", failed.Phase, createdAtUtc, failed.FailedAtUtc, null, null, null, failed.Code, failed.Message),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
}
