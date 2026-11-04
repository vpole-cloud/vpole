using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Contracts;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Persistence;

public sealed record SecretDeclaration
{
    public required string ResourceGroup { get; init; }

    public string? IsolationScope { get; init; }

    public string Purpose { get; init; } = "";

    public required string[] Keys { get; init; }
}

internal sealed partial class DeclarativeSecretProvider(
    IMongoCollection<Secret> secrets,
    IGitOpsMutations git,
    LoadIsolationScopePlanningSnapshot loadPlanningScope,
    FenceIsolationScopePlanningSnapshot fencePlanningScope,
    LoadResourceRegistrationPlanningSnapshot loadRegistration,
    SecretPlanningFence planningFence,
    AdmitResourceScopeReference admitScope,
    IResourceRegistrar registrar,
    MongoDeleteStoredSecret deletion,
    StoreSecretChange publish,
    TimeProvider clock) : ITransactionalDeclarativeResourceProvider, IDeclarativeResourceLifecycleProvider,
    IDeclarativeRegisteredResourceProvider, IDeclarativeLifecycleWarningProvider,
    IDeclarativeRegistrationOwnershipProvider
{
    public string Kind => "Secret";
    public ResourceTypeId ResourceType => SecretResource.Type;

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        SecretDeclarationRejected rejected => rejected.Reason switch
        {
            SecretDeclarationError.ResourceGroupUnavailable => "ResourceGroupUnavailable",
            SecretDeclarationError.ExplicitAdoptionRequired => "ExplicitAdoptionRequired",
            SecretDeclarationError.ImmutablePlacementChange => "ImmutablePlacementChange",
            SecretDeclarationError.ResourceScopeUnavailable => "ResourceScopeUnavailable",
            SecretDeclarationError.ResourceRegistrationUnavailable => "ResourceRegistrationUnavailable",
            SecretDeclarationError.SecretKeysInUse => "SecretKeysInUse",
            SecretDeclarationError.ProviderManagedSecret => "ProviderManagedSecret",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretDeclaration>(spec);

        return new(new("ResourceGroup", value.ResourceGroup), value.Keys,
            value.IsolationScope is null ? [] :
                [new(new("IsolationScope", value.IsolationScope), "spec.isolationScope")], [], []);
    }

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup)
                                && (value.IsolationScope is null ||
                                    DeclarationNames.Valid(value.IsolationScope) && value.IsolationScope != "default")
                                && value.Purpose is { Length: <= SecretDeclarationRules.MaximumPurposeLength } &&
                                value.Keys is { Length: > 0 and <= SecretKeyReferenceRules.MaximumKeys } && value.Keys
                                    .Distinct()
                                    .Count() == value.Keys.Length &&
                                value.Keys.All(SecretKeyReferenceRules.ValidKey),
            "spec");

        return DeclarationJson.Write(value with
        {
            Keys = value.Keys.Order(StringComparer.Ordinal).ToArray(),
            Purpose = value.Purpose.Trim()
        });
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretDeclaration>(spec);
        return
        [
            new(DeclarationPhase.Prepare,
            [
                new(new("ResourceGroup", value.ResourceGroup), DeclarationPhase.Prepare),
                .. value.IsolationScope is null ? Array.Empty<DeclarationDependency>() :
                    [new(new("IsolationScope", value.IsolationScope), DeclarationPhase.Prepare)]
            ])
        ];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await secrets.Find(
            session,
            SecretFilters.TechnicalName(request.TenantId, request.Name)).FirstOrDefaultAsync(ct))?.Id;

    public Task<GitOperationResult> ApplyAsync(
        GitOperation operation,
        CancellationToken ct) => git.ExecuteAsync(operation,
        async (
            session,
            token) =>
        {
            var context = operation.Context;
            var spec = DeclarationJson.Read<SecretDeclaration>(operation.Declaration.Spec);
            var owned = await AcceptedScopeAsync(operation.Input, session, token);
            if (owned is null)
                return new(operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ResourceScopeUnavailable));

            var groupId = owned.ResourceGroupId;
            var current = await secrets.Find(session,
                SecretFilters.Identity(context.TenantId, operation.ResourceId)).FirstOrDefaultAsync(token);
            current ??= await secrets.Find(session,
                SecretFilters.TechnicalName(context.TenantId, operation.Declaration.Address.Name))
                .FirstOrDefaultAsync(token);

            if (current?.Management is ProviderManagedSecret)
                return new(operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ProviderManagedSecret));

            if (current is not null && current.Id != operation.ResourceId)
                return new(
                    operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ExplicitAdoptionRequired));

            if (current is not null && (current.ResourceGroupId != groupId
                                        || current.IsolationScopeId != owned.IsolationScopeId))
                return new(
                    current.Id,
                    new SecretDeclarationRejected(SecretDeclarationError.ImmutablePlacementChange));

            if (await admitScope(
                    new AdmitResourceScopeReferenceRequest(
                        TenantId: context.TenantId,
                        ResourceGroupId: groupId,
                        IsolationScopeId: owned.IsolationScopeId,
                        Consumer: null),
                    session: session,
                    cancellationToken: token) is ResourceScopeReferenceUnavailable)
                return new(
                    operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ResourceScopeUnavailable));

            if (current is null)
            {
                var created = SecretFactory.CreateMetadata(operation.ResourceId,
                    context.TenantId,
                    groupId,
                    owned.IsolationScopeId,
                    operation.Declaration.Address.Name,
                    spec.Purpose,
                    spec.Keys,
                    clock.GetUtcNow().UtcDateTime);

                if (await registrar.RegisterAsync(
                        new RegisterResourceRequest(
                            TenantId: context.TenantId,
                            ResourceGroupId: groupId,
                            Resource: new(SecretResource.Type, created.Id, created.Name)),
                        session: session,
                        cancellationToken: token) is ResourceRegistrationMutationError)
                    return new(
                        created.Id,
                        new SecretDeclarationRejected(SecretDeclarationError.ResourceRegistrationUnavailable));

                await secrets.InsertOneAsync(session, created, cancellationToken: token);
                await publish(session, created, new SecretCreated(), token);

                return new(created.Id, Changed: true);
            }

            var transition = current.UpdateDeclaredMetadata(spec.Purpose, spec.Keys, clock.GetUtcNow().UtcDateTime);

            if (transition is SecretDeclaredKeysInUse)
                return new(current.Id, new SecretDeclarationRejected(SecretDeclarationError.SecretKeysInUse));

            var updated = (Secret)transition.Value;

            if (updated == current)
                return new(current.Id);

            await secrets.ReplaceOneAsync(
                session,
                SecretFilters.Identity(context.TenantId, current.Id),
                updated,
                cancellationToken: token);
            await publish(session, updated, new SecretMetadataUpdated(), token);

            return new(current.Id, Changed: true);
        },
        ct);
}
