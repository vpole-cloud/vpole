using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Contracts;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.Workloads;
using Pole.Features.Secrets.Primitives;
using System.Text.Json.Serialization;

namespace Pole.Features.Secrets.Application.Persistence;

public sealed record SecretBindingDeclaration
{
    public required string Secret { get; init; }

    public required ResourceAddress Consumer { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<SecretBindingUsage>))]
    public SecretBindingUsage Usage { get; init; } = SecretBindingUsage.Runtime;

    public Dictionary<string, string> KeyMappings { get; init; } = [];
}

internal sealed partial class DeclarativeSecretBindingProvider(
    IMongoCollection<Secret> secrets,
    IEnumerable<SecretDeclarationConsumer> consumerCapabilities,
    IGitOpsMutations git,
    LoadIsolationScopePlanningSnapshot loadPlanningScope,
    FenceIsolationScopePlanningSnapshot fencePlanningScope,
    FenceRegisteredResource fenceConsumer,
    LoadResourceDeploymentPlanningSnapshot loadPlanningPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlanningPlacements,
    SecretPlanningFence planningFence,
    AdmitResourceScopeReference admitScope,
    AdmitResourceDeploymentReferences admitDeployments,
    StoreSecretChange publish,
    MongoRemoveSecretBinding deletion,
    TimeProvider clock) : ITransactionalDeclarativeResourceProvider,
    IDeclarativeAdoptionPreviewProvider, IDeclarativeResourceAdoptionProvider, IDeclarativeResourceLifecycleProvider
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

    private readonly IReadOnlyDictionary<string, SecretDeclarationConsumer> consumers =
        consumerCapabilities.ToDictionary(item => item.Kind, StringComparer.Ordinal);

    private ResourceTypeId ConsumerType(SecretBindingDeclaration spec) => consumers[spec.Consumer.Kind].ResourceType;

    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);

        return new(null,
            [],
            [
                new(
                    new("Secret", value.Secret),
                    "spec.secret"),
                new(
                    value.Consumer,
                    "spec.consumer"),
                .. value.KeyMappings.Keys.Select(key => new DeclaredReference(
                    new("Secret", value.Secret),
                    "spec.keyMappings",
                    key))
            ],
            ExclusiveRelations:
            [
                new(
                    SecretDeclarationRules.ConsumerBindingRelation,
                    new("Secret", value.Secret),
                    value.Consumer)
            ],
            AccessGrants:
            [
                new(
                    value.Consumer,
                    new("Secret", value.Secret),
                    value.KeyMappings.Keys.ToArray(),
                    value.KeyMappings.Count == 0)
            ]);
    }

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.Secret) && value.Consumer is not null &&
                                DeclarationNames.Valid(value.Consumer.Name) &&
                                consumers.TryGetValue(value.Consumer.Kind, out var consumer) &&
                                consumer.Usage == value.Usage &&
                                value.KeyMappings is { Count: <= SecretKeyReferenceRules.MaximumKeys } &&
                                value.KeyMappings.All(pair =>
                                    SecretKeyReferenceRules.ValidKey(pair.Key) &&
                                    SecretKeyReferenceRules.ValidKey(pair.Value))
                                && value.KeyMappings.Values.Distinct().Count() == value.KeyMappings.Count,
            "spec");

        return DeclarationJson.Write(value);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);

        return
        [
            new(DeclarationPhase.Bind,
            [
                new(new("Secret", value.Secret), DeclarationPhase.Prepare),
                new(value.Consumer, DeclarationPhase.Prepare)
            ])
        ];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var secret = await secrets.Find(
            session,
            SecretFilters.DeclarationBindingName(request.TenantId, request.Name)).FirstOrDefaultAsync(ct);

        return secret?.Bindings.SingleOrDefault(binding => binding.DeclarationName == request.Name)?.Id;
    }

    public Task<GitOperationResult> ApplyAsync(
        GitOperation operation,
        CancellationToken ct) => git.ExecuteAsync(operation,
        async (
            session,
            token) =>
        {
            var context = operation.Context;
            var spec = DeclarationJson.Read<SecretBindingDeclaration>(operation.Declaration.Spec);
            var id = await git.ResolveAcceptedAsync(
                new ResolveAcceptedGitReferenceRequest(
                    Context: context,
                    ConsumerResourceId: operation.ResourceId,
                    Address: new("Secret", spec.Secret)),
                session: session,
                ct: token);
            var consumerId = await git.ResolveAcceptedAsync(
                new ResolveAcceptedGitReferenceRequest(
                    Context: context,
                    ConsumerResourceId: operation.ResourceId,
                    Address: spec.Consumer),
                session: session,
                ct: token);

            if (id is null || consumerId is null)
                return new(
                    operation.ResourceId,
                    new SecretBindingDeclarationRejected(
                        SecretBindingDeclarationError.BindingPrerequisiteUnavailable));

            var secret = await secrets.Find(
                session,
                SecretFilters.Identity(context.TenantId, id)).FirstOrDefaultAsync(token);

            if (secret is null)
                return new(
                    operation.ResourceId,
                    new SecretBindingDeclarationRejected(SecretBindingDeclarationError.SecretUnavailable));

            var namedSecret = await secrets.Find(
                session,
                SecretFilters.DeclarationBindingName(
                    context.TenantId,
                    operation.Declaration.Address.Name)).FirstOrDefaultAsync(token);

            if (namedSecret is not null && namedSecret.Id != id)
                return new(
                    operation.ResourceId,
                    new SecretBindingDeclarationRejected(SecretBindingDeclarationError.ImmutableBindingChange));

            var binding = SecretBinding.Create(operation.ResourceId,
                    ConsumerType(spec).Value,
                    consumerId,
                    new AllSecretDeployments(),
                    spec.KeyMappings, spec.Usage) with
            {
                DeclarationName = operation.Declaration.Address.Name
            };
            var transition = secret.DeclareBinding(binding, clock.GetUtcNow().UtcDateTime);

            return transition switch
            {
                Secret updated => await PersistAsync(updated),
                SecretBindingUsageChanged => Rejected(SecretBindingDeclarationError.ImmutableBindingChange),
                SecretBindingConsumerChanged => Rejected(SecretBindingDeclarationError.ImmutableBindingChange),
                SecretBindingConsumerConflict => Rejected(SecretBindingDeclarationError.ExplicitAdoptionRequired),
                UnknownSecretBindingKeys => Rejected(SecretBindingDeclarationError.SecretKeyUnavailable),
                SecretBindingCapacityExceeded => Rejected(SecretBindingDeclarationError.SecretBindingLimitReached),
                SecretBindingKeysInUse => Rejected(SecretBindingDeclarationError.SecretKeysInUse)
            };

            GitOperationResult Rejected(SecretBindingDeclarationError reason) =>
                new(operation.ResourceId, new SecretBindingDeclarationRejected(reason));

            async Task<GitOperationResult> PersistAsync(Secret updated)
            {
                if (await admitScope(
                        new AdmitResourceScopeReferenceRequest(
                            TenantId: context.TenantId,
                            ResourceGroupId: secret.ResourceGroupId,
                            IsolationScopeId: secret.IsolationScopeId,
                            Consumer: new ResourceScopeConsumerReference(ConsumerType(spec), consumerId)),
                        session: session,
                        cancellationToken: token) is ResourceScopeReferenceUnavailable ||
                    await admitDeployments(
                        new AdmitResourceDeploymentReferencesRequest(
                            TenantId: context.TenantId,
                            ResourceGroupId: secret.ResourceGroupId,
                            IsolationScopeId: secret.IsolationScopeId,
                            ConsumerType: ConsumerType(spec),
                            ConsumerId: consumerId,
                            AllDeployments: true,
                            DeploymentIds: []),
                        session: session,
                        cancellationToken: token) is ResourceDeploymentReferenceUnavailable)
                    return new(
                        operation.ResourceId,
                        new SecretBindingDeclarationRejected(
                            SecretBindingDeclarationError.BindingScopeUnavailable));

                if (updated == secret)
                    return new(operation.ResourceId);

                await secrets.ReplaceOneAsync(
                    session,
                    SecretFilters.Identity(context.TenantId, id),
                    updated,
                    cancellationToken: token);
                await publish(session, updated, new SecretBindingAdded(binding.Id), token);

                return new(operation.ResourceId, Changed: true);
            }
        },
        ct);
}
