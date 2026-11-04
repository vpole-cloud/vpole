using Pole.Features.Secrets.Contracts;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Persistence;

public sealed record SecretBindingDeclaration
{
    public required string Secret { get; init; }
    public required string WebService { get; init; }
    public Dictionary<string, string> KeyMappings { get; init; } = [];
}

internal sealed class DeclarativeSecretBindingProvider(
    IMongoCollection<Secret> secrets,
    IGitOpsMutations git,
    AdmitResourceScopeReference admitScope,
    AdmitResourceDeploymentReferences admitDeployments,
    StoreSecretChange publish,
    TimeProvider clock) : IDeclarativeResourceProvider
{
    public string Kind => "SecretBinding";
    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        SecretBindingDeclarationRejected rejected => rejected.Reason switch
        {
            SecretBindingDeclarationError.BindingPrerequisiteUnavailable => "BindingPrerequisiteUnavailable",
            SecretBindingDeclarationError.SecretUnavailable => "SecretUnavailable",
            SecretBindingDeclarationError.ImmutableBindingChange => "ImmutableBindingChange",
            SecretBindingDeclarationError.ExplicitAdoptionRequired => "ExplicitAdoptionRequired",
            SecretBindingDeclarationError.SecretKeyUnavailable => "SecretKeyUnavailable",
            SecretBindingDeclarationError.BindingScopeUnavailable => "BindingScopeUnavailable",
            SecretBindingDeclarationError.SecretBindingLimitReached => "SecretBindingLimitReached",
            SecretBindingDeclarationError.SecretKeysInUse => "SecretKeysInUse",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };
    private static ResourceTypeId ConsumerType => ResourceTypeId.Create(SecretDeclarationRules.ConsumerResourceType);
    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);
        return new(null, [], [new(new("Secret", value.Secret), "spec.secret"), new(new("WebService", value.WebService), "spec.webService"),
            .. value.KeyMappings.Keys.Select(key => new DeclaredReference(new("Secret", value.Secret), "spec.keyMappings", key))],
            ExclusiveRelations: [new(SecretDeclarationRules.ConsumerBindingRelation, new("Secret", value.Secret), new("WebService", value.WebService))],
            AccessGrants: [new(new("WebService", value.WebService), new("Secret", value.Secret), value.KeyMappings.Keys.ToArray(), value.KeyMappings.Count == 0)]);
    }
    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.Secret) && DeclarationNames.Valid(value.WebService) &&
            value.KeyMappings is { Count: <= SecretKeyReferenceRules.MaximumKeys } && value.KeyMappings.All(pair =>
                SecretKeyReferenceRules.ValidKey(pair.Key) && SecretKeyReferenceRules.ValidKey(pair.Value)) && value.KeyMappings.Values.Distinct().Count() == value.KeyMappings.Count, "spec");
        return DeclarationJson.Write(value);
    }
    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);
        return [new(DeclarationPhase.Bind, [new(new("Secret", value.Secret), DeclarationPhase.Prepare),
            new(new("WebService", value.WebService), DeclarationPhase.Prepare)])];
    }
    public async Task<string?> ResolveAsync(string tenantId, string name, IClientSessionHandle session, CancellationToken ct)
    {
        var secret = await secrets.Find(session, SecretFilters.DeclarationBindingName(tenantId, name)).FirstOrDefaultAsync(ct);
        return secret?.Bindings.SingleOrDefault(binding => binding.DeclarationName == name)?.Id;
    }
    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) => git.ExecuteAsync(operation, async (session, token) =>
    {
        var context = operation.Context;
        var spec = DeclarationJson.Read<SecretBindingDeclaration>(operation.Declaration.Spec);
        var id = await git.ResolveAsync(context.TenantId, new("Secret", spec.Secret), session, token);
        var consumerId = await git.ResolveAsync(context.TenantId, new("WebService", spec.WebService), session, token);
        if (id is null || consumerId is null) return new(operation.ResourceId, new SecretBindingDeclarationRejected(SecretBindingDeclarationError.BindingPrerequisiteUnavailable));
        var secret = await secrets.Find(session, SecretFilters.Identity(context.TenantId, id)).FirstOrDefaultAsync(token);
        if (secret is null) return new(operation.ResourceId, new SecretBindingDeclarationRejected(SecretBindingDeclarationError.SecretUnavailable));
        var namedSecret = await secrets.Find(session, SecretFilters.DeclarationBindingName(context.TenantId, operation.Declaration.Address.Name)).FirstOrDefaultAsync(token);
        if (namedSecret is not null && namedSecret.Id != id) return new(operation.ResourceId, new SecretBindingDeclarationRejected(SecretBindingDeclarationError.ImmutableBindingChange));
        var binding = SecretBinding.Create(operation.ResourceId, ConsumerType.Value, consumerId,
            new AllSecretDeployments(), spec.KeyMappings) with
        { DeclarationName = operation.Declaration.Address.Name };
        var transition = secret.DeclareBinding(binding, clock.GetUtcNow().UtcDateTime);
        if (transition is SecretBindingRejected rejected)
            return new(operation.ResourceId, new SecretBindingDeclarationRejected(rejected.Reason switch
            {
                SecretBindingRejection.ImmutableConsumer => SecretBindingDeclarationError.ImmutableBindingChange,
                SecretBindingRejection.ConsumerAlreadyBound => SecretBindingDeclarationError.ExplicitAdoptionRequired,
                SecretBindingRejection.UnknownKey => SecretBindingDeclarationError.SecretKeyUnavailable,
                SecretBindingRejection.LimitReached => SecretBindingDeclarationError.SecretBindingLimitReached,
                SecretBindingRejection.KeysInUse => SecretBindingDeclarationError.SecretKeysInUse,
                _ => throw new ArgumentOutOfRangeException(nameof(rejected))
            }));
        if (await admitScope(context.TenantId, secret.ResourceGroupId, secret.IsolationScopeId,
                new ResourceScopeConsumerReference(ConsumerType, consumerId), session, token) is ResourceScopeReferenceUnavailable ||
            await admitDeployments(context.TenantId, secret.ResourceGroupId, secret.IsolationScopeId,
                ConsumerType, consumerId, true, [], session, token) is ResourceDeploymentReferenceUnavailable)
            return new(operation.ResourceId, new SecretBindingDeclarationRejected(SecretBindingDeclarationError.BindingScopeUnavailable));
        var updated = (Secret)transition.Value;
        if (updated == secret) return new(operation.ResourceId);
        await secrets.ReplaceOneAsync(session, SecretFilters.Identity(context.TenantId, id), updated, cancellationToken: token);
        await publish(session, updated, new SecretBindingAdded(binding.Id), token);
        return new(operation.ResourceId, Changed: true);
    }, ct);
}
