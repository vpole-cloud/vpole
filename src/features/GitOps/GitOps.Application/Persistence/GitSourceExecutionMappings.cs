using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

public static class GitSourceExecutionMappings
{
    public static GitMutationContext ToContext(this GitSourceExecution source) => new(
        source.TenantId,
        source.SourceId,
        source.RunId,
        source.Fence,
        source.SourceUrl,
        source.ActorUserId,
        source.ActorEmail,
        source.Attempt);
}
