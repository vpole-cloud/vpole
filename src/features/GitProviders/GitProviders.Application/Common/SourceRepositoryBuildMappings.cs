using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Common;

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
