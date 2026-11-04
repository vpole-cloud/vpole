using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Application.Planning.Mongo.Filters;
using Pole.Features.GitOps.Application.Runs.Mongo;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Planning.Mongo;

public sealed record PinSnapshotReferencesRequest(
    GitRunHandle Handle,
    GitAcceptedSnapshot Snapshot,
    CompiledSnapshot Compiled);

public sealed class GitAcceptedReferences(
    IMongoCollection<GitAcceptedReference> references,
    GitRunStore runs,
    IServiceProvider services,
    MongoRunTransaction transactions)
{
    public async Task<bool> PinAsync(PinSnapshotReferencesRequest request, CancellationToken ct)
    {
        var providers = services.GetRequiredService<DeclarativeProviderRegistry>();
        var resources = request.Snapshot.Resources.ToDictionary(item => (item.Kind, item.Name));
        foreach (var declaration in request.Compiled.Declarations)
        {
            var consumer = resources[(declaration.Address.Kind, declaration.Address.Name)];
            var facts = providers.Find(declaration.Address.Kind)!.Facts(declaration.Spec);
            var addresses = facts.References.Select(item => item.Target)
                .Concat(facts.Scope is { } scope ? [scope] : [])
                .Concat(declaration.Steps.SelectMany(item => item.Dependencies).Select(item => item.Resource))
                .Distinct();
            foreach (var address in addresses)
            {
                if (resources.ContainsKey((address.Kind, address.Name)))
                    continue;

                var pinned = await transactions.InvokeOutcomeAsync<bool>(async (tx, token) =>
                    {
                        var run = await runs.TouchAsync(request.Handle, tx.Session, token);
                        if (run?.AcceptedSnapshot is not { } accepted ||
                            accepted.DesiredHash != request.Snapshot.DesiredHash)
                            return tx.Abort(false);

                        var key = GitAcceptedReferenceFilters.Key(run.Stamp.Source,
                            consumer.ResourceId,
                            address.Kind,
                            address.Name);
                        if (await references.Find(tx.Session, GitAcceptedReferenceFilters.Identity(key))
                                .AnyAsync(token))
                            return tx.Commit(true);

                        var provider = providers.Find(address.Kind);
                        var id = provider is null
                            ? null
                            : await provider.ResolveAsync(
                                new(run.Stamp.Source.TenantId, address.Name),
                                tx.Session,
                                token);
                        if (id is not null)
                            await references.InsertOneAsync(tx.Session,
                                new(key, run.Stamp.Source, consumer.ResourceId, new(address.Kind, address.Name, id)),
                                cancellationToken: token);
                        return tx.Commit(true);
                    },
                    ct);
                if (!pinned)
                    return false;
            }
        }

        return true;
    }

    // Provider-owned technical dependencies (such as placement profiles) use the same durable identity rule.
    // This runs before the provider transaction, so later blockers cannot roll back a successful resolution.
    public Task<PinGitReferenceOutcome> PinAsync(PinGitReferenceRequest request, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<PinGitReferenceOutcome>(async (tx, token) =>
            {
                var handle = request.Context.ToHandle();
                var run = await runs.TouchAsync(handle, tx.Session, token);
                if (run?.AcceptedSnapshot is not { } accepted ||
                    !accepted.Resources.Any(item => item.ResourceId == request.ConsumerResourceId))
                    return tx.Abort(new StaleGitExecution());

                var target = request.Target;
                var key = GitAcceptedReferenceFilters.Key(run.Stamp.Source,
                    request.ConsumerResourceId,
                    target.Address.Kind,
                    target.Address.Name);
                var current = await references.Find(tx.Session, GitAcceptedReferenceFilters.Identity(key))
                    .FirstOrDefaultAsync(token);
                if (current is not null)
                    return tx.Commit(new GitReferencePinned(current.Target.ResourceId));

                await references.InsertOneAsync(tx.Session,
                    new(key,
                        run.Stamp.Source,
                        request.ConsumerResourceId,
                        new(target.Address.Kind, target.Address.Name, target.ResourceId)),
                    cancellationToken: token);
                return tx.Commit(new GitReferencePinned(target.ResourceId));
            },
            ct);

    public async Task<string?> ResolveAsync(
        ResolveAcceptedGitReferenceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var run = await runs.TouchAsync(request.Context.ToHandle(), session, ct);
        if (run?.AcceptedSnapshot is not { } accepted ||
            !accepted.Resources.Any(item => item.ResourceId == request.ConsumerResourceId))
            return null;

        var internalResource = accepted.Resources.SingleOrDefault(item =>
            item.Kind == request.Address.Kind && item.Name == request.Address.Name);
        if (internalResource is not null)
            return internalResource.ResourceId;

        var key = GitAcceptedReferenceFilters.Key(run.Stamp.Source,
            request.ConsumerResourceId,
            request.Address.Kind,
            request.Address.Name);
        var reference = await references.Find(session, GitAcceptedReferenceFilters.Identity(key))
            .FirstOrDefaultAsync(ct);
        return reference?.Target.ResourceId;
    }
}
