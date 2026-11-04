using MongoDB.Bson;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

public enum SnapshotApplicationStatus
{
    InvalidSnapshot,
    StaleExecution,
    SourceRelocationRequired,
    UnsupportedStoredSchema,
    ConfigurationApplied,
    Blocked,
    Applying
}

public sealed record SnapshotApplicationResult(
    CompiledSnapshot Snapshot,
    GitMutationContext? Context,
    IReadOnlyList<GitOperationResult> Operations,
    SnapshotApplicationStatus Status);

// Application orchestration for the authenticated manual-snapshot API and durable resume jobs.
// Repository fetching and the general durable source worker remain separate follow-up stages.
public sealed class DeclarativePipeline(
    ManifestCompiler compiler,
    DeclarativeProviderRegistry providers,
    AcceptSnapshot acceptSnapshot,
    ClaimSnapshotAttempt claimAttempt,
    RejectStoredSnapshotSchema rejectSchema,
    FinalizeSnapshotAttempt finalizeAttempt)
{
    public async Task<SnapshotApplicationResult> ApplyAsync(string tenantId, string sourceId, string sourceUrl,
        string commit, long expectedFence, string actorUserId, string actorEmail,
        IReadOnlyList<ManifestFile> files, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(tenantId, out _) || !ObjectId.TryParse(sourceId, out _) || expectedFence is < 0 or long.MaxValue ||
            string.IsNullOrWhiteSpace(actorUserId) || string.IsNullOrWhiteSpace(actorEmail) ||
            !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new ArgumentException("An authorized tenant/source execution context is required.");
        tenantId = ObjectId.Parse(tenantId).ToString();
        sourceId = ObjectId.Parse(sourceId).ToString();
        var snapshot = compiler.Compile(files);
        if (!snapshot.Valid) return new(snapshot, null, [], SnapshotApplicationStatus.InvalidSnapshot);
        var context = new GitMutationContext(tenantId, sourceId, ObjectId.GenerateNewId().ToString(),
            checked(expectedFence + 1), sourceUrl, actorUserId, actorEmail);
        var referencedSecrets = snapshot.Declarations.SelectMany(item => providers.Find(item.Address.Kind)!.Facts(item.Spec).References)
            .Where(item => item.Target.Kind == "Secret").Select(item => item.Target.Name).Distinct().ToArray();
        var acceptance = await acceptSnapshot(snapshot, context, commit, expectedFence, referencedSecrets, ct);
        if (acceptance != SnapshotAcceptance.Accepted)
            return new(snapshot, null, [], acceptance switch
            {
                SnapshotAcceptance.StaleExecution => SnapshotApplicationStatus.StaleExecution,
                SnapshotAcceptance.SourceRelocationRequired => SnapshotApplicationStatus.SourceRelocationRequired,
                _ => throw new InvalidOperationException("An accepted snapshot cannot have a rejection code.")
            });
        return await ResumeAsync(context, ct);
    }

    public async Task<SnapshotApplicationResult> ResumeAsync(GitMutationContext context, CancellationToken ct = default, bool onlyIfDue = false)
    {
        if (!ObjectId.TryParse(context.TenantId, out var tenant) || !ObjectId.TryParse(context.SourceId, out var sourceId))
            return new(new([], []), context, [], SnapshotApplicationStatus.StaleExecution);
        context = context with { TenantId = tenant.ToString(), SourceId = sourceId.ToString() };
        var source = await claimAttempt(context, onlyIfDue, ct);
        if (source is null) return new(new([], []), context, [], SnapshotApplicationStatus.StaleExecution);
        context = source.ToContext();
        var accepted = AcceptedSnapshotCodec.Read(source);
        var snapshot = compiler.Restore(source.ApiVersion, accepted.Select(item => new AcceptedManifestDeclaration(item.Kind, item.Name, item.SpecJson)).ToArray());
        if (!snapshot.Valid)
            return new(snapshot, context, [], await rejectSchema(source, ct) ? SnapshotApplicationStatus.UnsupportedStoredSchema : SnapshotApplicationStatus.StaleExecution);
        if (!DeclarationPlan.TryOrder(snapshot.Declarations, out var plan))
            throw new InvalidOperationException("The stored operation graph is invalid.");
        var results = new List<GitOperationResult>();
        foreach (var step in plan)
        {
            var id = accepted.Single(item => item.Kind == step.Declaration.Address.Kind && item.Name == step.Declaration.Address.Name).ResourceId;
            var operation = new GitOperation(context, step.Declaration, step.Phase, id);
            results.Add(await providers.Find(step.Declaration.Address.Kind)!.ApplyAsync(operation, ct));
            if (results[^1].Failure is StaleGitExecution) break;
        }
        var outcome = results.Any(item => item.Failure is StaleGitExecution) ? SnapshotApplyOutcome.StaleExecution :
            results.All(item => item.Applied) ? SnapshotApplyOutcome.Applied : SnapshotApplyOutcome.Blocked;
        return (await finalizeAttempt(source, outcome, ct)) switch
        {
            FinalizedSnapshotAttempt finalized => new(snapshot, context, results, finalized.Progress switch
            {
                ApplyingSnapshot => SnapshotApplicationStatus.Applying,
                BlockedSnapshot => SnapshotApplicationStatus.Blocked,
                AppliedSnapshot => SnapshotApplicationStatus.ConfigurationApplied,
                UnsupportedSnapshotSchema => SnapshotApplicationStatus.UnsupportedStoredSchema,
                StaleSnapshotExecution => SnapshotApplicationStatus.StaleExecution
            }),
            SupersededSnapshotAttempt => new(snapshot, context, results, SnapshotApplicationStatus.StaleExecution)
        };
    }
}
