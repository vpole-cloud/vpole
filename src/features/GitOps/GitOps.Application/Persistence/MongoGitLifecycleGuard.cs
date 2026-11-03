using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Persistence;

public sealed class MongoGitLifecycleGuard(
    GitRunStore runs,
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<GitOperationReceipt> receipts,
    GitLifecycleCleanupProofs cleanupProofs,
    IServiceProvider services) : IGitLifecyclePlanGuard
{
    private GitLifecycleResources Resources => services.GetRequiredService<GitLifecycleResources>();
    private GitLifecyclePreviewReferences References => services.GetRequiredService<GitLifecyclePreviewReferences>();

    internal async Task<IDeclarationFailure?> ValidateApplyAsync(GitOperation operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var run = await runs.TouchAsync(operation.Context.ToHandle(), session, ct);
        if (run?.Input is not GitInputLifecycle lifecycle)
            return null;
        var subject = Subject(operation);
        var action = lifecycle.Plan.Operations.SingleOrDefault(item => item.Resource == subject ||
            item.Action == GitLifecycleAction.Replace && item.Replacement == subject);
        if (action is null || action.Action is not (GitLifecycleAction.Apply or GitLifecycleAction.Adopt or
                GitLifecycleAction.Replace))
            return new GitLifecycleApprovalStale();
        var context = await ReadAsync(Authority(operation.Context, lifecycle.Plan, action.Id), session, ct);
        if (context is null)
            return new StaleGitExecution();
        if (context.Plan.Authorization is GitPreviewPolicyAuthorized)
            return null;
        if (context.Synchronization.Commits.Observed != operation.Context.Authority.Commit)
            return new GitLifecycleApprovalStale();

        var unfinished = Unfinished(context);
        var required = RequiredResources(context, unfinished);
        var references = AdvanceReferences(context).Where(item => unfinished.Contains(item.ConsumerResourceId))
            .ToArray();
        if (!await References.ValidateAsync(context.Plan.Source, references, session, ct))
            return new GitLifecycleApprovalStale();
        foreach (var approved in context.Operations)
        {
            var target = approved.Action == GitLifecycleAction.Replace ? approved.Replacement! : approved.Resource;
            if (!required.Contains(target.ResourceId))
                continue;
            var expected = GitLifecycleCleanupProofs.Advance(ApplicationBaseline(context, approved, target),
                context.CleanupVersions);
            var readAction = approved with
            {
                Resource = target,
                Action = approved.Action == GitLifecycleAction.Adopt
                    ? GitLifecycleAction.Adopt : GitLifecycleAction.Apply,
                Replacement = null,
                Expected = expected
            };
            var current = await References.PreconditionsAsync(new(context.Plan.Source, readAction, references),
                session, ct);
            if (!GitLifecycleResources.Same(expected, current))
                return new GitLifecycleApprovalStale();
        }
        return null;
    }

    internal async Task<GitLifecycleEffect?> CaptureApplyAsync(GitOperation operation, IClientSessionHandle session,
        CancellationToken ct)
    {
        var run = await runs.TouchAsync(operation.Context.ToHandle(), session, ct);
        if (run?.Input is not GitInputLifecycle lifecycle)
            return null;
        var subject = Subject(operation);
        var action = lifecycle.Plan.Operations.Single(item => item.Resource == subject || item.Replacement == subject);
        var referenceVersions = await References.RefreshVersionsAsync(
            new(run.Stamp.Source.TenantId, lifecycle.Plan.References), session, ct);
        var related = new List<GitResourceVersion>();
        GitResourcePreconditions? postcondition = null;
        foreach (var approved in lifecycle.Plan.Operations)
        {
            var targets = approved.Replacement is { } next ? new[] { approved.Resource, next } : [approved.Resource];
            foreach (var target in targets)
            {
                var readAction = approved with
                {
                    Resource = target,
                    Action = approved.Action == GitLifecycleAction.Adopt && target == approved.Resource
                        ? GitLifecycleAction.Adopt : GitLifecycleAction.Apply,
                    Replacement = null
                };
                var observed = await References.PreconditionsAsync(new(lifecycle.Plan.Source, readAction,
                    referenceVersions), session, ct);
                related.AddRange(observed?.Versions ?? []);
                if (target == subject)
                    postcondition = observed;
            }
        }
        if (lifecycle.Plan.Source.Input is GitPreviewRepositoryInput input)
        {
            var providers = services.GetRequiredService<DeclarativeProviderRegistry>();
            foreach (var consumer in input.Snapshot.Resources)
            {
                // Fallback resolution only returns accepted preconditions; it is not a fresh owner read.
                if (providers.Find(consumer.Kind) is not IDeclarativePreviewReferenceProvider)
                    continue;
                var resolved = await References.ResolveAsync(run.Stamp.Source.TenantId, input.Snapshot, consumer,
                    referenceVersions, session, ct);
                if (resolved is GitPreviewReferencesSnapshot metadata)
                    related.AddRange(metadata.References.Where(reference =>
                            providers.Find(reference.Address.Kind) is not IDeclarativeResourceLifecycleProvider)
                        .SelectMany(item => item.Version?.Documents ?? [])
                        .Select(item => new GitResourceVersion(item.DocumentId, item.Revision)));
            }
        }
        foreach (var reference in referenceVersions.Where(item =>
                     item.State == GitPreviewReferenceState.Existing))
        {
            var current = await Resources.ReadAsync(run.Stamp.Source.TenantId, reference.Target, session, ct);
            if (current is GitLifecycleResourceSnapshot found)
                related.AddRange(found.Version.Documents.Select(item => new GitResourceVersion(item.DocumentId,
                    item.Revision)));
        }
        var state = await states.Find(session, GitRepositoryFilters.ActiveState(run.Stamp)).SingleAsync(ct);
        return new(run.MutationVersion, action.Id, GitLifecycleStep.Apply, postcondition, state.InventoryRevision,
            GitLifecyclePreviewReferences.Merge(related), subject);
    }

    private HashSet<string> RequiredResources(ApprovedLifecycleExecution context, HashSet<string> unfinished)
    {
        var required = unfinished.ToHashSet(StringComparer.Ordinal);
        if (context.Run.AcceptedSnapshot is not { } snapshot)
            return required;
        var compiled = services.GetRequiredService<ManifestCompiler>().Restore(snapshot.ApiVersion,
            snapshot.Resources.Select(item => new AcceptedManifestDeclaration(item.Kind, item.Name, item.SpecJson))
                .ToArray());
        var providers = services.GetRequiredService<DeclarativeProviderRegistry>();
        foreach (var consumer in snapshot.Resources.Where(item => unfinished.Contains(item.ResourceId)))
        {
            var declaration = compiled.Declarations.Single(item => item.Address.Kind == consumer.Kind &&
                item.Address.Name == consumer.Name);
            var facts = providers.Find(consumer.Kind)!.Facts(declaration.Spec);
            var addresses = facts.References.Select(item => item.Target)
                .Concat(facts.Scope is { } scope ? [scope] : [])
                .Concat(declaration.Steps.SelectMany(item => item.Dependencies).Select(item => item.Resource));
            foreach (var address in addresses)
                if (snapshot.Resources.SingleOrDefault(item => item.Kind == address.Kind && item.Name == address.Name)
                    is { } target)
                    required.Add(target.ResourceId);
        }
        return required;
    }

    private static HashSet<string> Unfinished(ApprovedLifecycleExecution context) =>
        (context.Run.AcceptedSnapshot?.Resources ?? []).Where(resource =>
        {
            var final = context.Run.AcceptedSnapshot!.Operations.Last(item => item.ResourceId == resource.ResourceId);
            return !context.Receipts.Any(receipt => receipt.Lifecycle?.Step == GitLifecycleStep.Apply &&
                receipt.Blocker is null && receipt.ResourceId == resource.ResourceId &&
                receipt.Phase == final.Phase.ToString() && receipt.DesiredHash == resource.Hash);
        }).Select(item => item.ResourceId).ToHashSet(StringComparer.Ordinal);

    private static GitResourcePreconditions? ApplicationBaseline(ApprovedLifecycleExecution context,
        GitPreviewOperation operation, GitResourceIdentity target)
    {
        // ReadAsync has already advanced original subjects through every committed receipt vector.
        if (operation.Resource == target)
            return operation.Expected;

        var effects = context.Receipts.Where(item => item.Fence == context.Run.Stamp.Fence &&
                item.Blocker is null && item.Lifecycle is not null)
            .Select(item => item.Lifecycle!).OrderBy(item => item.Sequence).ToArray();
        var own = effects.LastOrDefault(item => item.Subject == target);
        if (own?.Postcondition is not { } baseline)
            return null;

        // A replacement's new subject has its own baseline; later sibling operations may legitimately
        // fence shared dependencies. Advance only their receipt-proven versions, never live values.
        var versions = effects.Where(item => item.Sequence > own.Sequence)
            .SelectMany(item => item.RelatedVersions.Concat(item.Postcondition?.Versions ?? []))
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        return baseline with
        {
            Versions = baseline.Versions.Select(item => versions.GetValueOrDefault(item.DocumentId) ?? item)
                .ToImmutableArray()
        };
    }

    private static IReadOnlyList<GitPreviewPinnedReference> AdvanceReferences(ApprovedLifecycleExecution context)
    {
        var versions = context.Receipts.Where(item => item.Lifecycle is not null && item.Blocker is null)
            .Select(item => item.Lifecycle!).OrderBy(item => item.Sequence)
            .SelectMany(item => item.RelatedVersions.Concat(item.Postcondition?.Versions ?? []))
            .Concat(context.CleanupVersions)
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        return context.Plan.References.Select(reference => reference with
        {
            Versions = reference.Versions.Select(version => versions.GetValueOrDefault(version.DocumentId) ?? version)
                .ToImmutableArray()
        }).ToArray();
    }

    private static GitResourceIdentity Subject(GitOperation operation) =>
        new(operation.Declaration.Address.Kind, operation.Declaration.Address.Name, operation.ResourceId);

    private static GitLifecycleAuthority Authority(GitMutationContext context, GitLifecycleExecutionPlan plan,
        string action) => new(context.TenantId, context.SourceId, context.RunId,
        context.Authority.ConfigurationRevision, context.Authority.Generation, context.Fence,
        context.Authority.WorkerId, context.Authority.Attempt, plan.PreviewId, action, context.Authority.Commit);

    public async Task<ReadLifecycleApprovedScopeOutcome> ReadScopeAsync(
        GitLifecycleAuthority authority,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var context = await ReadAsync(authority, session, cancellationToken);
        return context is null ? new StaleGitExecution() : new GitLifecycleApprovedScope(
            context.Operations.Select(operation => new GitLifecycleApprovedResource(
                new(new(operation.Resource.Kind, operation.Resource.Name), operation.Resource.ResourceId),
                operation.Action,
                operation.Expected is { } expected ? new DeclarationVersionToken(expected.Versions.Select(item =>
                    new DeclarationDocumentVersion(item.DocumentId, item.Revision)).ToArray()) : null)).ToArray());
    }

    public async Task<bool> AuthorizeAsync(
        AuthorizeGitResourceMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (request.Lifecycle is not { } authority || request.Context is not null ||
            authority.TenantId != request.TenantId)
            return false;

        var context = await ReadAsync(authority, session, cancellationToken);
        var action = context?.Operations.SingleOrDefault(item => item.Id == authority.ActionId);
        if (action is null || action.Resource.Kind != request.Kind ||
            action.Resource.ResourceId != request.ResourceId ||
            (request.LifecycleEffect switch
            {
                GitLifecycleMutationEffect.Adopt => action.Action != GitLifecycleAction.Adopt,
                GitLifecycleMutationEffect.Remove => action.Action is not
                    (GitLifecycleAction.Remove or GitLifecycleAction.Replace),
                null => true,
                GitPreviewAwaitingConfirmation => true
            }))
            return false;

        var owner = await inventory.Find(session,
            ManagedDeclarationFilters.Resource(request.TenantId, request.Kind, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (owner?.SourceId != action.Expected?.OwnerSourceId ||
            owner is not null && owner.SourceId != authority.SourceId)
            return false;

        if (owner is null)
            return action.Action is GitLifecycleAction.Adopt or GitLifecycleAction.Replace;

        var written = await inventory.UpdateOneAsync(session,
            ManagedDeclarationFilters.Resource(request.TenantId, request.Kind, request.ResourceId),
            Builders<ManagedDeclaration>.Update.Inc(item => item.MutationFence, 1),
            cancellationToken: cancellationToken);
        return written.ModifiedCount == 1;
    }

    internal async Task<ApprovedLifecycleExecution?> ReadAsync(
        GitLifecycleAuthority authority,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        GitTransactionGuard.Require(session);
        var run = await runs.TouchAsync(authority.ToHandle(), session, cancellationToken);
        if (run?.Input is not GitInputLifecycle lifecycle || run.Commit != authority.Commit ||
            lifecycle.Plan.PreviewId != authority.PreviewId ||
            !lifecycle.Plan.Operations.Any(item => item.Id == authority.ActionId) ||
            (lifecycle.Plan.Authorization switch
            {
                GitPreviewConfirmed human => human.RunId != run.Id,
                GitPreviewPolicyAuthorized policy => policy.RunId != run.Id ||
                    policy.ConfigurationRevision != run.Stamp.ConfigurationRevision,
                GitPreviewAwaitingConfirmation => true
            }))
            return null;

        var stored = await receipts.Find(session, GitRepositoryFilters.Receipts(authority.TenantId,
            authority.SourceId, authority.RunId)).ToListAsync(cancellationToken);
        var effects = stored.Where(item => item.Fence == authority.Fence && item.Lifecycle is not null &&
                item.Blocker is null).Select(item => item.Lifecycle!).OrderBy(item => item.Sequence).ToArray();
        if (effects.Any(effect => !lifecycle.Plan.Operations.Any(item => item.Id == effect.ActionId)))
            throw new InvalidOperationException("A lifecycle receipt must belong to its immutable action plan.");

        var state = await states.Find(session, GitRepositoryFilters.ActiveState(run.Stamp))
            .SingleAsync(cancellationToken);
        var expectedInventory = effects.Length == 0
            ? lifecycle.Plan.Source.InventoryRevision
            : effects.Max(item => item.InventoryRevision);
        if (state.InventoryRevision != expectedInventory)
            return null;

        var operations = GitLifecyclePreconditionAdvancement.Advance(lifecycle.Plan.Operations, effects);
        var cleanupVersions = await cleanupProofs.AdvanceAsync(run, lifecycle.Plan, stored, operations,
            session, cancellationToken);
        operations = operations.Select(item => item with
        {
            Expected = GitLifecycleCleanupProofs.Advance(item.Expected, cleanupVersions)
        }).ToImmutableArray();
        return new(run, lifecycle.Plan, operations, stored, state, cleanupVersions);
    }
}

internal sealed record ApprovedLifecycleExecution(
    GitReconciliationRun Run,
    GitLifecycleExecutionPlan Plan,
    IReadOnlyList<GitPreviewOperation> Operations,
    IReadOnlyList<GitOperationReceipt> Receipts,
    GitSynchronizationState Synchronization,
    IReadOnlyList<GitResourceVersion> CleanupVersions);
