using Pole.Features.GitOps.Compiler;
using System.Collections.Immutable;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Planning;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Jobs;

internal sealed record GitLifecycleRepositoryDeferred : IDeclarationFailure;

public sealed class GitAcceptedSnapshotExecutor(
    GitRunStore runs,
    DeclarativeProviderRegistry providers,
    ManifestCompiler compiler,
    GitAcceptedReferences references,
    GitOperationPlanning planning,
    IGitOpsMutations mutations,
    RepositoryProviderRegistry repositories)
{
    public async Task<RepositoryExecutionReport> ExecuteAsync(GitWorkClaim claim, GitAcceptedSnapshot input,
        CancellationToken ct)
    {
        var restored = compiler.Restore(input.ApiVersion,
            input.Resources.Select(item =>
                new AcceptedManifestDeclaration(item.Kind, item.Name, item.SpecJson)).ToArray());
        if (!restored.Valid || restored.Declarations.Any(item => !input.Resources.Any(accepted =>
                accepted.Kind == item.Address.Kind && accepted.Name == item.Address.Name &&
                accepted.Hash == item.Hash)))
            return await runs.CompleteAsync(claim.Handle, GitRunCompletion.Failed,
                [new("StoredSnapshotInvalid", null, null, null)], ct)
                ? new(RepositoryExecutionDisposition.Handled, null, []) : Lost();

        if (!await references.PinAsync(new PinSnapshotReferencesRequest(claim.Handle, input, restored), ct))
            return Lost();

        var context = claim.ToContext(input.Commit);
        var declarations = restored.Declarations.ToDictionary(item => (item.Address.Kind, item.Address.Name));
        var results = new List<GitOperationResult>();
        var diagnosticsList = new List<GitRunDiagnostic>();
        foreach (var descriptor in input.Operations)
        {
            ct.ThrowIfCancellationRequested();
            var declaration = declarations[(descriptor.Kind, descriptor.Name)];
            var provider = providers.Find(descriptor.Kind)
                           ?? throw new InvalidOperationException(
                               "An accepted declaration provider is no longer registered.");
            var inputOperation = new GitOperationInput(context, declaration, descriptor.Phase, descriptor.ResourceId);
            var result = await ApplyPlannedAsync(inputOperation, provider,
                (claim.Run.Input as GitInputLifecycle)?.Plan, ct);
            results.Add(result);
            if (result.Failure is GitLifecycleRepositoryDeferred)
                return await runs.DelayAsync(claim.Handle,
                    new(nameof(RepositoryTemporarilyUnavailable), null, null, null), ct)
                    ? new(RepositoryExecutionDisposition.Handled, context, results) : Lost();
            if (result.Failure is StaleGitExecution)
                return new(RepositoryExecutionDisposition.LeaseLost, context, results);
            if (result.Failure is DeclarationPreconditionsChanged)
                return await runs.DelayAsync(claim.Handle,
                    new(nameof(DeclarationPreconditionsChanged), declaration.File, declaration.Document, null), ct)
                    ? new(RepositoryExecutionDisposition.Handled, context, results)
                    : Lost();
            if (result.Failure is not null)
                diagnosticsList.Add(new(DeclarationFailureDiagnostics.Code(result.Failure, provider)!,
                    declaration.File,
                    declaration.Document,
                    null));
        }

        var completion = results.All(item => item.Applied)
            ? GitRunCompletion.ConfigurationApplied
            : GitRunCompletion.Blocked;
        return new(await runs.CompleteAsync(claim.Handle, completion, diagnosticsList.ToImmutableArray(), ct)
                ? RepositoryExecutionDisposition.Handled
                : RepositoryExecutionDisposition.LeaseLost,
            context,
            results);
    }

    private async Task<GitOperationResult> ApplyPlannedAsync(GitOperationInput input,
        IDeclarativeResourceProvider provider, GitLifecycleExecutionPlan? lifecycle, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var outcome = await planning.PlanAsync(input, ct);
            if (outcome is StaleGitExecution stale)
                return new(input.ResourceId, stale);
            if (outcome is GitOperationReplayed replay)
                return replay.Result;

            if (lifecycle?.Authorization is GitPreviewConfirmed)
            {
                var access = await CheckApprovalHeadAsync(input.Context.TenantId, lifecycle, ct);
                if (access is LifecycleAccessTransient)
                    return new(input.ResourceId, new GitLifecycleRepositoryDeferred());
                if (access is LifecycleAccessRejected)
                    return new(input.ResourceId, new GitLifecycleApprovalStale());
            }
            var operation = ((GitOperationPlanned)outcome.Value).Operation;
            var blocker = DeclarationPlanValues.Blocker(operation.Plan.Decision);
            var result = blocker is not null || operation.Plan.Decision is NoOpDeclarationPlan
                ? await mutations.ExecuteAsync(operation,
                    (_, _) => Task.FromResult(new GitOperationResult(input.ResourceId, blocker)), ct)
                : await provider.ApplyAsync(operation, ct);
            if (result.Failure is not DeclarationPreconditionsChanged)
                return result;
        }

        return new(input.ResourceId, new DeclarationPreconditionsChanged());
    }

    private async Task<LifecycleAccessOutcome> CheckApprovalHeadAsync(string tenant,
        GitLifecycleExecutionPlan plan, CancellationToken ct)
    {
        if (plan.Source.Input is not GitPreviewRepositoryInput input)
            return new LifecycleAccessGranted();
        var provider = repositories.Find(input.Repository.Provider);
        if (provider is null)
            return new LifecycleAccessRejected("RepositoryProviderUnavailable");
        var reference = new RepositorySourceReference(tenant, input.Repository.ConnectionId,
            input.Repository.ExternalRepositoryId);
        var access = await provider.CheckAccessAsync(reference, ct);
        if (access is RepositoryTemporarilyUnavailable)
            return new LifecycleAccessTransient();
        if (access is not RepositoryAccessGranted)
            return new LifecycleAccessRejected("RepositoryAccessUnavailable");
        var head = await provider.ResolveCommitAsync(new(reference, input.Repository.Branch), ct);
        return head switch
        {
            RepositoryTemporarilyUnavailable => new LifecycleAccessTransient(),
            RepositoryCommitResolved commit when commit.Commit == input.Snapshot.Commit => new LifecycleAccessGranted(),
            _ => new LifecycleAccessRejected(nameof(GitLifecycleApprovalStale))
        };
    }

    private static RepositoryExecutionReport Lost() => new(RepositoryExecutionDisposition.LeaseLost, null, []);
}
