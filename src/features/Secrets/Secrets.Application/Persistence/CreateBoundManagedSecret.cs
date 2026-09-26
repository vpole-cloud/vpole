using System.Text.Json;
using Pole.Encryption;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class MongoCreateBoundManagedSecret(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    AdmitResourceScopeReference admitScope,
    AdmitResourceDeploymentReferences admitDeployments,
    IResourceRegistrar registrar,
    StoreSecretChange publish,
    TimeProvider clock)
{
    public async Task<Result<Unit, ManagedSecretError>> InvokeAsync(
        PreparedManagedSecret prepared,
        string resourceGroupId,
        string isolationScopeId,
        string deploymentId,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction || prepared.Version != 1)
            throw new InvalidOperationException("Managed Secret creation requires a transaction and initial version.");
        resourceGroupId = SecretIdentity.ObjectId(resourceGroupId);
        isolationScopeId = SecretIdentity.ObjectId(isolationScopeId);
        deploymentId = SecretIdentity.ObjectId(deploymentId);
        var consumerType = ResourceTypeId.Create(prepared.Owner.ResourceType);
        if (await admitScope(prepared.TenantId, resourceGroupId, isolationScopeId,
                new(consumerType, prepared.Owner.ResourceId), session, ct) is ResourceScopeReferenceUnavailable ||
            await admitDeployments(prepared.TenantId, resourceGroupId, isolationScopeId, consumerType,
                prepared.Owner.ResourceId, false, [deploymentId], session, ct) is ResourceDeploymentReferenceUnavailable)
            return new Err<ManagedSecretError>(new ManagedSecretReferenceRejected());
        var now = clock.GetUtcNow().UtcDateTime;
        var binding = SecretBinding.Create(ObjectId.GenerateNewId().ToString(), prepared.Owner.ResourceType,
            prepared.Owner.ResourceId, new AllSecretDeployments(), []);
        var secret = SecretFactory.CreateManaged(prepared.SecretId, prepared.TenantId, resourceGroupId, isolationScopeId,
            prepared.Name, "Provider-managed credentials", prepared.Keys, binding,
            prepared.KubernetesType == ManagedSecretKubernetesType.BasicAuthentication
                ? KubernetesSecretType.BasicAuthentication : KubernetesSecretType.Opaque, now);
        if (await registrar.RegisterAsync(secret.TenantId, secret.ResourceGroupId,
                new(SecretResource.Type, secret.Id, secret.Name), session, ct) is ResourceRegistrationMutationError)
            return new Err<ManagedSecretError>(new ManagedSecretReferenceRejected());
        try
        {
            await secrets.InsertOneAsync(session, secret, cancellationToken: ct);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new Err<ManagedSecretError>(new ManagedSecretChanged());
        }
        await versions.InsertOneAsync(session, new SecretVersion($"{secret.Id}:1", secret.TenantId, secret.Id, 1,
            JsonSerializer.Deserialize<EncryptedValue>(prepared.EncryptedValues.AsSpan())!, now), cancellationToken: ct);
        await publish(session, secret, new SecretCreated(), ct);
        return new Ok<Unit>(Unit.Value);
    }
}
