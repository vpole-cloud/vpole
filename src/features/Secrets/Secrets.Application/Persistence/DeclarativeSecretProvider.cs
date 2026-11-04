using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Contracts;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Persistence;

public sealed record SecretDeclaration
{
    public required string ResourceGroup { get; init; }

    public string Purpose { get; init; } = "";

    public required string[] Keys { get; init; }
}

internal sealed partial class DeclarativeSecretProvider(
    IMongoCollection<Secret> secrets,
    IGitOpsMutations git,
    GetResourceGroupOwnership groupOwnership,
    LoadResourceGroupPlanningSnapshot loadPlanningGroup,
    FenceResourceGroupPlanningSnapshot fencePlanningGroup,
    SecretPlanningFence planningFence,
    AdmitResourceScopeReference admitScope,
    IResourceRegistrar registrar,
    StoreSecretChange publish,
    TimeProvider clock) : ITransactionalDeclarativeResourceProvider
{
    public string Kind => "Secret";

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
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretDeclaration>(spec);

        return new(new("ResourceGroup", value.ResourceGroup), value.Keys, [], [], []);
    }

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup)
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

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) =>
    [
        new(
            DeclarationPhase.Prepare,
            [
                new(
                    new("ResourceGroup", DeclarationJson.Read<SecretDeclaration>(spec).ResourceGroup),
                    DeclarationPhase.Prepare)
            ])
    ];

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
            var groupId = await git.ResolveAcceptedAsync(
                new ResolveAcceptedGitReferenceRequest(
                    Context: context,
                    ConsumerResourceId: operation.ResourceId,
                    Address: new("ResourceGroup", spec.ResourceGroup)),
                session: session,
                ct: token);

            if (groupId is null)
                return new(
                    operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ResourceGroupUnavailable));

            var group = await loadPlanningGroup(new(context.TenantId, groupId), session, token);

            if (group is not ResourceGroupPlanningSnapshot owned)
                return new(
                    operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ResourceGroupUnavailable));

            var current = await secrets.Find(session,
                SecretFilters.Identity(context.TenantId, operation.ResourceId)).FirstOrDefaultAsync(token);
            current ??= await secrets.Find(session,
                SecretFilters.TechnicalName(context.TenantId, operation.Declaration.Address.Name))
                .FirstOrDefaultAsync(token);

            if (current is not null && current.Id != operation.ResourceId)
                return new(
                    operation.ResourceId,
                    new SecretDeclarationRejected(SecretDeclarationError.ExplicitAdoptionRequired));

            if (current is not null && (current.ResourceGroupId != groupId
                                        || current.IsolationScopeId != owned.DefaultIsolationScopeId))
                return new(
                    current.Id,
                    new SecretDeclarationRejected(SecretDeclarationError.ImmutablePlacementChange));

            if (await admitScope(
                    new AdmitResourceScopeReferenceRequest(
                        TenantId: context.TenantId,
                        ResourceGroupId: groupId,
                        IsolationScopeId: owned.DefaultIsolationScopeId,
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
                    owned.DefaultIsolationScopeId,
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
