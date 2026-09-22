using System.Text.Json;
using Pole.Encryption;
using MongoDB.Driver;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class MongoRotateManagedSecret(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    StoreSecretChange publish,
    TimeProvider clock)
{
    public async Task<Result<Unit, ManagedSecretError>> InvokeAsync(
        PreparedManagedSecret prepared,
        long expectedVersion,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction || prepared.Version != checked(expectedVersion + 1))
            throw new InvalidOperationException("Managed Secret rotation requires a transaction and consecutive version.");
        var current = await secrets.Find(session, ManagedSecretFilters.Owner(prepared.TenantId, prepared.Owner)).FirstOrDefaultAsync(ct);
        if (current is null)
            return new Err<ManagedSecretError>(new ManagedSecretUnavailable());
        var projectionType = ((ProviderManagedSecret)current.Management).ProjectionType == SecretProjectionType.BasicAuthentication
            ? ManagedSecretProjectionType.BasicAuthentication : ManagedSecretProjectionType.Opaque;
        if (projectionType != prepared.ProjectionType || current.Id != prepared.SecretId || current.CurrentVersion != expectedVersion || current.Name != prepared.Name ||
            current.Bindings.Any(x => x.KeyMappings.Keys.Except(prepared.Keys).Any()))
            return new Err<ManagedSecretError>(new ManagedSecretChanged());
        var now = clock.GetUtcNow().UtcDateTime;
        var rotated = current.Rotate(prepared.Keys, now);
        await secrets.ReplaceOneAsync(session, SecretFilters.Identity(current.TenantId, current.Id), rotated, cancellationToken: ct);
        await versions.InsertOneAsync(session, new SecretVersion($"{current.Id}:{prepared.Version}", current.TenantId,
            current.Id, prepared.Version, JsonSerializer.Deserialize<EncryptedValue>(prepared.EncryptedValues.AsSpan())!, now), cancellationToken: ct);
        await publish(session, rotated, new SecretRotated(), ct);
        return new Ok<Unit>(Unit.Value);
    }
}
