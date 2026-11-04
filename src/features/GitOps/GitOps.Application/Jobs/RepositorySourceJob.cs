using System.Collections.Immutable;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Jobs;

public enum RepositoryExecutionDisposition { NoWork, Handled, LeaseLost }

public sealed record RepositoryExecutionReport(
    RepositoryExecutionDisposition Disposition,
    GitMutationContext? Context,
    IReadOnlyList<GitOperationResult> Operations);

// Hangfire stores only these server-owned identifiers. Mongo is the source of scheduling and lease truth.
public sealed class RepositorySourceJob(
    GitRunStore runs,
    RepositoryProviderRegistry repositories,
    ManifestCompiler compiler,
    GitSnapshotAcceptance acceptance,
    GitAcceptedSnapshotExecutor snapshotExecutor,
    GitLifecycleExecutor lifecycle,
    GitPrunePlanning prune,
    IOptions<GitOpsWorkerOptions> options,
    TimeProvider clock,
    ILogger<RepositorySourceJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(string tenantId, string sourceId, CancellationToken ct) =>
        await ProcessAsync(new(tenantId, sourceId), ct);

    public async Task<RepositoryExecutionReport> ProcessAsync(GitSourceIdentity source, CancellationToken ct)
    {
        var outcome = await runs.ClaimAsync(source, Guid.NewGuid().ToString("N"), ct);
        if (outcome is not GitWorkClaim claim)
            return new(RepositoryExecutionDisposition.NoWork, null, []);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = KeepLeaseAsync(claim.Handle, execution);
        try
        {
            return await ExecuteClaimAsync(claim, execution.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && execution.IsCancellationRequested)
        {
            return new(RepositoryExecutionDisposition.LeaseLost, null, []);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Retain bounded recovery state, while letting unexpected failures reach the normal job error path.
            await runs.DelayAsync(claim.Handle, new("ExecutionInfrastructureFailure", null, null, null), ct);
            throw;
        }
        finally
        {
            await execution.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested)
            {
            }
        }
    }

    private async Task KeepLeaseAsync(GitRunHandle handle, CancellationTokenSource execution)
    {
        try
        {
            while (!execution.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.HeartbeatSeconds), clock, execution.Token);
                if (!await runs.HeartbeatAsync(handle, execution.Token))
                {
                    await execution.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "GitOps heartbeat failed for run {RunId}", handle.Stamp.RunId);
            await execution.CancelAsync();
            throw;
        }
    }

    private async Task<RepositoryExecutionReport> ExecuteClaimAsync(GitWorkClaim claim, CancellationToken ct)
    {
        if (claim.Run.Input is GitInputLifecycle)
            return await lifecycle.ExecuteAsync(claim, ct);

        var repository = repositories.Find(claim.Run.Repository.Provider);
        if (repository is null)
            return await FailAsync(claim.Handle, "RepositoryProviderUnavailable", ct);

        var location = claim.Run.Repository;
        var reference = new RepositorySourceReference(claim.Source.TenantId,
            location.ConnectionId,
            location.ExternalRepositoryId);
        var access = await repository.CheckAccessAsync(reference, ct);
        if (access is not RepositoryAccessGranted)
            return await HandleAccessFailureAsync(claim.Handle, access, ct);

        var run = claim.Run;
        if (run.Input is GitInputUnresolved)
        {
            var resolution = await repository.ResolveCommitAsync(new(reference, location.Branch), ct);
            if (resolution is not RepositoryCommitResolved resolved)
                return await HandleResolveFailureAsync(claim.Handle, resolution, ct);

            if (!await runs.PinAsync(claim.Handle, resolved.Commit, ct))
                return Lost();
            run = run.Pin(resolved.Commit);
        }

        if (run.Input is GitInputPinned pinned)
        {

            if (run.RetryOfRunId is null && claim.PreviouslyAppliedSnapshot is { } previous &&
                previous.Commit == pinned.Commit)
            {
                if (!await runs.AcceptAsync(claim.Handle, previous, ct))
                    return Lost();
                run = run.Accept(previous);
            }
            else
            {
                var read = await repository.ReadSnapshotAsync(new(reference, pinned.Commit, location.ManifestDirectory),
                    ct);
                if (read is not RepositorySnapshotRead snapshot)
                    return await HandleReadFailureAsync(claim.Handle, read, ct);
                if (snapshot.Commit != pinned.Commit)
                    return await FailAsync(claim.Handle, "RepositoryCommitMismatch", ct);

                var compiled = compiler.Compile(snapshot.Files.Select(item => new ManifestFile(item.Path, item.Content))
                    .ToArray());
                if (!compiled.Valid)
                {
                    var diagnostics = compiled.Diagnostics.Select(item =>
                        new GitRunDiagnostic(item.Code, item.File, item.Document, item.Field)).ToImmutableArray();
                    return await runs.CompleteAsync(claim.Handle, GitRunCompletion.Failed, diagnostics, ct)
                        ? new(RepositoryExecutionDisposition.Handled, null, [])
                        : Lost();
                }

                var accepted = await acceptance.BuildAsync(claim with { Run = run }, compiled, ct);
                if (!await runs.AcceptAsync(claim.Handle, accepted, ct))
                    return Lost();
                run = run.Accept(accepted);
            }
        }

        var input = ((GitInputAccepted)run.Input).Snapshot;
        // An accepted retry may be old. Its current tenant/connection access is required again before mutations.
        access = await repository.CheckAccessAsync(reference, ct);
        if (access is not RepositoryAccessGranted)
            return await HandleAccessFailureAsync(claim.Handle, access, ct);
        var admitted = await prune.PlanAsync(claim with { Run = run }, ct);
        return admitted switch
        {
            GitPruneNotRequired => await snapshotExecutor.ExecuteAsync(claim, input, ct),
            GitPruneAuthorized authorized => await lifecycle.ExecuteAsync(authorized.Claim, ct),
            GitPruneAwaitingConfirmation or GitPruneBlocked => new(RepositoryExecutionDisposition.Handled, null, []),
            StaleGitExecution => Lost()
        };
    }

    private Task<RepositoryExecutionReport> HandleAccessFailureAsync(
        GitRunHandle handle,
        CheckRepositoryAccessOutcome outcome,
        CancellationToken ct) => outcome switch
        {
            RepositoryAccessDenied => FailAsync(handle, nameof(RepositoryAccessDenied), ct),
            RepositoryNotFound => FailAsync(handle, nameof(RepositoryNotFound), ct),
            RepositoryTemporarilyUnavailable => DelayAsync(handle, ct),
            RepositoryAccessGranted => throw new InvalidOperationException("Successful access was already handled.")
        };

    private Task<RepositoryExecutionReport> HandleResolveFailureAsync(
        GitRunHandle handle,
        ResolveRepositoryCommitOutcome outcome,
        CancellationToken ct) => outcome switch
        {
            RepositoryAccessDenied => FailAsync(handle, nameof(RepositoryAccessDenied), ct),
            RepositoryNotFound => FailAsync(handle, nameof(RepositoryNotFound), ct),
            RepositoryRefNotFound => FailAsync(handle, nameof(RepositoryRefNotFound), ct),
            RepositoryTemporarilyUnavailable => DelayAsync(handle, ct),
            RepositoryCommitResolved => throw new InvalidOperationException("Resolved commit was already handled.")
        };

    private Task<RepositoryExecutionReport> HandleReadFailureAsync(
        GitRunHandle handle,
        ReadRepositorySnapshotOutcome outcome,
        CancellationToken ct) => outcome switch
        {
            RepositoryAccessDenied => FailAsync(handle, nameof(RepositoryAccessDenied), ct),
            RepositoryNotFound => FailAsync(handle, nameof(RepositoryNotFound), ct),
            RepositoryRefNotFound => FailAsync(handle, nameof(RepositoryRefNotFound), ct),
            RepositoryDirectoryNotFound => FailAsync(handle, nameof(RepositoryDirectoryNotFound), ct),
            RepositorySnapshotRejected rejected => FailAsync(handle, rejected.Reason.ToString(), ct),
            RepositoryTemporarilyUnavailable => DelayAsync(handle, ct),
            RepositorySnapshotRead => throw new InvalidOperationException("Successful snapshot was already handled.")
        };

    private async Task<RepositoryExecutionReport> FailAsync(GitRunHandle handle, string code, CancellationToken ct) =>
        await runs.CompleteAsync(handle, GitRunCompletion.Failed, [new(code, null, null, null)], ct)
            ? new(RepositoryExecutionDisposition.Handled, null, [])
            : Lost();

    private async Task<RepositoryExecutionReport> DelayAsync(GitRunHandle handle, CancellationToken ct) =>
        await runs.DelayAsync(handle, new(nameof(RepositoryTemporarilyUnavailable), null, null, null), ct)
            ? new(RepositoryExecutionDisposition.Handled, null, [])
            : Lost();

    private static RepositoryExecutionReport Lost() => new(RepositoryExecutionDisposition.LeaseLost, null, []);
}
