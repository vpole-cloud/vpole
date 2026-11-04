using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

internal static class GitOperationAuthority
{
    public static bool Matches(GitReconciliationRun? run, GitOperationInput operation) =>
        run?.Input is GitInputAccepted accepted && run.Commit == operation.Context.Authority.Commit &&
        accepted.Snapshot.Resources.Any(item => item.Kind == operation.Declaration.Address.Kind &&
                                               item.Name == operation.Declaration.Address.Name &&
                                               item.ResourceId == operation.ResourceId &&
                                               item.Hash == operation.Declaration.Hash) &&
        accepted.Snapshot.Operations.Any(item => item.Kind == operation.Declaration.Address.Kind &&
                                                item.Name == operation.Declaration.Address.Name &&
                                                item.ResourceId == operation.ResourceId &&
                                                item.Phase == operation.Phase);
}
