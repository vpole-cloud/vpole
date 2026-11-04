using System.Text.Json;
using Pole.Encryption;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
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
    public async Task<ManagedSecretMutationOutcome> InvokeAsync(
        CreateBoundManagedSecretRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction || request.Prepared.Version != 1)
            throw new InvalidOperationException("Managed Secret creation requires a transaction and initial version.");
        request = request with
        {
            ResourceGroupId = SecretIdentity.ObjectId(request.ResourceGroupId),
            IsolationScopeId = SecretIdentity.ObjectId(request.IsolationScopeId),
            DeploymentId = SecretIdentity.ObjectId(request.DeploymentId)
        };
        var consumerType = ResourceTypeId.Create(request.Prepared.Owner.ResourceType);
        if (await admitScope(
            new AdmitResourceScopeReferenceRequest(
                TenantId: request.Prepared.TenantId,
                ResourceGroupId: request.ResourceGroupId,
                IsolationScopeId: request.IsolationScopeId,
                Consumer: new(consumerType, request.Prepared.Owner.ResourceId)),
            session: session,
            cancellationToken: ct) is ResourceScopeReferenceUnavailable ||
            await admitDeployments(
                new AdmitResourceDeploymentReferencesRequest(
                    TenantId: request.Prepared.TenantId,
                    ResourceGroupId: request.ResourceGroupId,
                    IsolationScopeId: request.IsolationScopeId,
                    ConsumerType: consumerType,
                    ConsumerId: request.Prepared.Owner.ResourceId,
                    AllDeployments: false,
                    DeploymentIds: [request.DeploymentId]),
                session: session,
                cancellationToken: ct) is ResourceDeploymentReferenceUnavailable)
            return new ManagedSecretReferenceRejected();
        var now = clock.GetUtcNow().UtcDateTime;
        var binding = SecretBinding.Create(ObjectId.GenerateNewId().ToString(), request.Prepared.Owner.ResourceType,
            request.Prepared.Owner.ResourceId, new AllSecretDeployments(), []);
        var secret = SecretFactory.CreateManaged(request.Prepared.SecretId, request.Prepared.TenantId,
            request.ResourceGroupId, request.IsolationScopeId,
            request.Prepared.Name, "Provider-managed credentials", request.Prepared.Keys, binding,
            request.Prepared.KubernetesType == ManagedSecretKubernetesType.BasicAuthentication
                ? KubernetesSecretType.BasicAuthentication : KubernetesSecretType.Opaque, now);
        if (await registrar.RegisterAsync(
            new RegisterResourceRequest(
                TenantId: secret.TenantId,
                ResourceGroupId: secret.ResourceGroupId,
                Resource: new(SecretResource.Type, secret.Id, secret.Name)),
            session: session,
            cancellationToken: ct) is ResourceRegistrationMutationError)
            return new ManagedSecretReferenceRejected();
        try
        {
            await secrets.InsertOneAsync(session, secret, cancellationToken: ct);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new ManagedSecretChanged();
        }
        await versions.InsertOneAsync(session, new SecretVersion($"{secret.Id}:1", secret.TenantId, secret.Id, 1,
            JsonSerializer.Deserialize<EncryptedValue>(request.Prepared.EncryptedValues.AsSpan())!, now),
                cancellationToken: ct);
        await publish(session, secret, new SecretCreated(), ct);
        return new ManagedSecretMutationSucceeded();
    }
}
