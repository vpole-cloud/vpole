using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application;

public sealed class SecretStore(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    MongoRunTransaction transactions,
    IEnvelopeEncryptionService encryption,
    AdmitResourceScopeReference admitScope,
    AdmitResourceDeploymentReferences admitDeployments,
    IResourceRegistrar registrar,
    LoadResourceDeploymentSet loadDeployments,
    StoreOutboxWrites outbox,
    TimeProvider clock)
{
    public static readonly ResourceTypeId ResourceType = ResourceTypeId.Create("pole/secret");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task<Secret?> GetAsync(
        string tenantId,
        string id,
        CancellationToken ct) =>
        secrets.Find(SecretFilters.Identity(tenantId, id)).FirstOrDefaultAsync(ct)!;

    public async Task<SecretPage> ListAsync(
        string tenantId,
        string? after,
        int pageSize,
        CancellationToken ct)
    {
        var filter = SecretFilters.Page(tenantId, after);
        var page = await secrets.Find(filter).SortBy(x => x.Id).Limit(pageSize + 1).ToListAsync(ct);
        var items = page.Take(pageSize).Select(SecretMetadata.From).ToArray();
        return new SecretPage(items, page.Count > pageSize ? items[^1].Id : null);
    }

    public async Task<SecretMutationOutcome> CreateAsync(
        string tenantId,
        string groupId,
        string scopeId,
        string name,
        string purpose,
        Dictionary<string, string> values,
        CancellationToken ct)
    {
        tenantId = SecretIdentity.ObjectId(tenantId);
        groupId = SecretIdentity.ObjectId(groupId);
        scopeId = SecretIdentity.ObjectId(scopeId);
        var now = Now;
        var secret = new Secret(
            ObjectId.GenerateNewId().ToString(),
            tenantId,
            groupId,
            scopeId,
            name.Trim(),
            name.Trim().ToLowerInvariant(),
            purpose.Trim(),
            values.Keys.Order().ToArray(),
            1,
            1,
            "Active",
            [],
            now,
            now);
        var version = await EncryptAsync(secret, values, ct);
        if (version is null)
            return new SecretError(new SecretEncryptionFailed());
        return await transactions.InvokeOutcomeAsync<SecretMutationOutcome>(async (tx, token) =>
        {
            if (await admitScope(tenantId, groupId, scopeId, null, tx.Session, token) is Err<ResourceScopeReferenceUnavailable>)
                return tx.Abort(new SecretError(new SecretReferenceRejected()));

            var registration = await registrar.RegisterAsync(
                tenantId,
                groupId,
                new(ResourceType, secret.Id, secret.Name),
                tx.Session,
                token);
            if (registration is Err<ResourceRegistrationMutationError>)
                return tx.Abort(new SecretError(new SecretReferenceRejected()));
            try
            {
                await secrets.InsertOneAsync(tx.Session, secret, cancellationToken: token);
            }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return tx.Abort(new SecretError(new SecretConflict()));
            }
            await versions.InsertOneAsync(tx.Session, version, cancellationToken: token);
            await PublishAsync(tx.Session, secret, "Created", null, token);
            return tx.Commit(SecretMetadata.From(secret));
        }, ct);
    }

    public Task<SecretMutationOutcome> RotateAsync(
        string tenantId,
        string id,
        long expectedVersion,
        Dictionary<string, string> values,
        CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<SecretMutationOutcome>(async (tx, token) =>
        {
            var current = await secrets.Find(tx.Session, SecretFilters.Identity(tenantId, id)).FirstOrDefaultAsync(token);
            if (current is null)
                return tx.Abort(new SecretError(new SecretMissing()));
            if (current.CurrentVersion != expectedVersion ||
                current.Bindings.Any(x => x.KeyMappings.Keys.Except(values.Keys).Any()))
                return tx.Abort(new SecretError(new SecretConflict()));

            var rotated = current.Rotate(values.Keys.Order().ToArray(), Now);
            var version = await EncryptAsync(rotated, values, token);
            if (version is null)
                return tx.Abort(new SecretError(new SecretEncryptionFailed()));
            // Acquire the aggregate write conflict before the immutable version insert. A competing
            // rotation retries its snapshot and returns a version conflict, never a duplicate-key 5xx.
            await secrets.ReplaceOneAsync(
                tx.Session,
                SecretFilters.Identity(tenantId, id),
                rotated,
                cancellationToken: token);
            await versions.InsertOneAsync(tx.Session, version, cancellationToken: token);
            await PublishAsync(tx.Session, rotated, "Rotated", null, token);
            return tx.Commit(SecretMetadata.From(rotated));
        }, ct);

    public Task<SecretMutationOutcome> BindAsync(
        string tenantId,
        string id,
        SecretBinding binding,
        CancellationToken ct)
    {
        binding = SecretIdentity.Binding(binding);
        return transactions.InvokeOutcomeAsync<SecretMutationOutcome>(async (tx, token) =>
        {
            var current = await secrets.Find(tx.Session, SecretFilters.Identity(tenantId, id)).FirstOrDefaultAsync(token);
            if (current is null)
                return tx.Abort(new SecretError(new SecretMissing()));
            if (current.Bindings.Length >= Secret.MaximumBindings ||
                current.Bindings.Any(x => x.ConsumerType == binding.ConsumerType && x.ConsumerId == binding.ConsumerId) ||
                binding.KeyMappings.Keys.Except(current.KeyNames).Any())
                return tx.Abort(new SecretError(new SecretConflict()));

            var type = ResourceTypeId.Create(binding.ConsumerType);
            if (await admitScope(
                current.TenantId,
                current.ResourceGroupId,
                current.IsolationScopeId,
                new ResourceScopeConsumerReference(type, binding.ConsumerId),
                tx.Session,
                token) is Err<ResourceScopeReferenceUnavailable> ||
                await admitDeployments(
                    current.TenantId,
                    current.ResourceGroupId,
                    current.IsolationScopeId,
                    type,
                    binding.ConsumerId,
                    binding.AllDeployments,
                    binding.DeploymentIds,
                    tx.Session,
                    token) is Err<ResourceDeploymentReferenceUnavailable>)
                return tx.Abort(new SecretError(new SecretReferenceRejected()));

            var bound = current.Bind(binding, Now);
            await secrets.ReplaceOneAsync(
                tx.Session,
                SecretFilters.Identity(tenantId, id),
                bound,
                cancellationToken: token);
            await PublishAsync(tx.Session, bound, "Bound", binding.Id, token);
            return tx.Commit(SecretMetadata.From(bound));
        }, ct);
    }

    public Task<SecretMutationOutcome> DetachAsync(
        string tenantId,
        string id,
        string bindingId,
        CancellationToken ct)
    {
        bindingId = SecretIdentity.ObjectId(bindingId);
        return transactions.InvokeOutcomeAsync<SecretMutationOutcome>(async (tx, token) =>
        {
            var current = await secrets.Find(tx.Session, SecretFilters.Identity(tenantId, id)).FirstOrDefaultAsync(token);
            if (current is null)
                return tx.Abort(new SecretError(new SecretMissing()));
            if (current.Bindings.All(x => x.Id != bindingId))
                return tx.Commit(SecretMetadata.From(current));

            var detached = current.Detach(bindingId, Now);
            await secrets.ReplaceOneAsync(
                tx.Session,
                SecretFilters.Identity(tenantId, id),
                detached,
                cancellationToken: token);
            await PublishAsync(tx.Session, detached, "Detached", bindingId, token);
            return tx.Commit(SecretMetadata.From(detached));
        }, ct);
    }

    public Task<SecretMutationOutcome> DeleteAsync(
        string tenantId,
        string id,
        CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<SecretMutationOutcome>(async (tx, token) =>
        {
            var current = await secrets.Find(tx.Session, SecretFilters.Identity(tenantId, id)).FirstOrDefaultAsync(token);
            if (current is null)
                return tx.Abort(new SecretError(new SecretMissing()));
            if (current.Bindings.Length != 0)
                return tx.Abort(new SecretError(new SecretConflict()));

            var unregistered = await registrar.UnregisterAsync(
                current.TenantId,
                current.ResourceGroupId,
                ResourceType,
                current.Id,
                tx.Session,
                token);
            if (unregistered is Err<ResourceRegistrationMutationError>)
                return tx.Abort(new SecretError(new SecretReferenceRejected()));
            await secrets.DeleteOneAsync(
                tx.Session,
                SecretFilters.Identity(tenantId, id),
                cancellationToken: token);
            await versions.DeleteManyAsync(
                tx.Session,
                SecretFilters.Versions(tenantId, id),
                cancellationToken: token);
            await PublishAsync(tx.Session, current with
            {
                Revision = current.Revision + 1
            }, "Deleted", null, token);
            return tx.Commit(new SecretDeleted());
        }, ct);

    public async Task<SecretUsage[]> UsageAsync(
        Secret secret,
        CancellationToken ct)
    {
        var usages = new List<SecretUsage>();
        foreach (var binding in secret.Bindings)
        {
            var deployments = await loadDeployments(
                secret.TenantId,
                ResourceTypeId.Create(binding.ConsumerType),
                binding.ConsumerId,
                ct);
            usages.Add(new(
                binding,
                deployments
                    .Where(x => x.IsolationScopeId == secret.IsolationScopeId &&
                        x.DesiredOperation != "Delete" &&
                        (binding.AllDeployments || binding.DeploymentIds.Contains(x.DeploymentId)))
                    .Select(x => x.DeploymentId)
                    .ToArray()));
        }
        return usages.ToArray();
    }

    public async Task CleanupConsumerAsync(
        string tenantId,
        string groupId,
        string resourceType,
        string resourceId,
        CancellationToken ct)
    {
        resourceType = ResourceTypeId.Create(resourceType).Value;
        resourceId = SecretIdentity.ObjectId(resourceId);
        var filter = SecretFilters.Consumer(tenantId, groupId, resourceType, resourceId);
        var candidates = await secrets.Find(filter).ToListAsync(ct);
        foreach (var candidate in candidates)
            await transactions.InvokeOutcomeAsync<Unit>(async (tx, token) =>
            {
                var current = await secrets.Find(tx.Session, SecretFilters.Identity(tenantId, candidate.Id)).FirstOrDefaultAsync(token);
                if (current is null)
                    return tx.Commit(Unit.Value);

                var removed = current.Bindings.Where(x => x.ConsumerType == resourceType && x.ConsumerId == resourceId).ToArray();
                if (removed.Length == 0)
                    return tx.Commit(Unit.Value);

                var updated = current;
                foreach (var binding in removed)
                    updated = updated.Detach(binding.Id, Now);
                await secrets.ReplaceOneAsync(
                    tx.Session,
                    SecretFilters.Identity(tenantId, current.Id),
                    updated,
                    cancellationToken: token);
                foreach (var binding in removed)
                    await PublishAsync(tx.Session, updated, "Detached", binding.Id, token);
                return tx.Commit(Unit.Value);
            }, ct);
    }

    private async Task<SecretVersion?> EncryptAsync(
        Secret secret,
        Dictionary<string, string> values,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            var result = await encryption.EncryptAsync(
                bytes,
                EnvelopeEncryptionContext.ForTenant(
                    secret.TenantId,
                    "Secret",
                    secret.Id,
                    "values",
                    secret.CurrentVersion),
                ct);
            return result switch
            {
                Ok<EncryptedValue> encrypted => new SecretVersion(
                    $"{secret.Id}:{secret.CurrentVersion}",
                    secret.TenantId,
                    secret.Id,
                    secret.CurrentVersion,
                    encrypted.Value,
                    Now),
                Err<EnvelopeEncryptionFailed> => null
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private Task PublishAsync(
        IClientSessionHandle session,
        Secret secret,
        string change,
        string? bindingId,
        CancellationToken ct) =>
        outbox(
            session,
            [
                OutboxWrite.Create(new SecretChangedEvent(
                    secret.Id,
                    secret.CurrentVersion,
                    secret.Revision,
                    change,
                    bindingId)
                {
                    EventCore = EventCore.Create(secret.TenantId)
                })
            ],
            ct);
}
