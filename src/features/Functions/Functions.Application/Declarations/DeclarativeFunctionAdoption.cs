using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using static Pole.Features.Functions.Application.Declarations.FunctionDeclarations;

namespace Pole.Features.Functions.Application.Declarations;

internal sealed class FunctionDeclarationAdoption(
    IMongoCollection<FunctionApp> resources,
    ResolveComputeProfilePlanningSnapshot loadCompute,
    FunctionDeclarationPlanning planning,
    FunctionDeclarationReferences referenceReader,
    FunctionDeclarationLifecycle lifecycleReader
)
{
    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var root = await resources.Find(session, FunctionAppFilters.ByTenantAndId(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (root is null || root.DesiredState == FunctionAppDesiredState.Deleting)
            return Blocked(FunctionDeclarationError.ResourceDeleting);
        if (root.NormalizedName != request.Declaration.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        var loaded = await planning.ReadCurrentStateAsync(root, session, cancellationToken);
        if (loaded is DeclarationReadBlocked currentBlocked)
            return currentBlocked;
        var current = (FunctionPlanningCurrent)loaded.Value;
        var technical = await referenceReader.ReadPreviewReferencesAsync(
            new ReadPreviewReferencesRequest(request.TenantId, request.Declaration, request.References),
            session,
            cancellationToken);
        if (technical is DeclarationReadBlocked rejected)
            return rejected;
        var additional = ((GitPreviewReferencesSnapshot)technical.Value).References;
        var references = request.References
            .Concat(additional)
            .GroupBy(item => item.Address)
            .Select(group => group.Last())
            .ToArray();
        var declaration = FunctionDeclarationJson.Read(request.Declaration.Spec);
        var groupId = PreviewReference(references, new ResourceAddress("ResourceGroup", declaration.ResourceGroup))!
            .ResourceId;
        var scopeId = PreviewReference(
            references,
            declaration.Placement.IsolationScope is { } scopeName
                ? new ResourceAddress("IsolationScope", scopeName)
                : new ResourceAddress("DefaultIsolationScope", declaration.ResourceGroup))!.ResourceId;
        var targetId = PreviewReference(
            references,
            new ResourceAddress("DeploymentTarget", declaration.Placement.DeploymentTarget))!.ResourceId;
        var computeId = PreviewReference(
            references,
            new ResourceAddress("ComputeProfile", declaration.Placement.ComputeProfile.Name))!.ResourceId;
        if (await loadCompute(
                new ComputeProfilePlanningRequest(
                    request.TenantId,
                    ResourceType,
                    computeId,
                    targetId,
                    declaration.Placement.ComputeProfile.Revision),
                session,
                cancellationToken) is not ComputeProfilePlanningSnapshot compute)
            return Blocked(FunctionDeclarationError.ComputeProfileUnavailable);

        var secrets = ImmutableDictionary.CreateBuilder<string, FunctionSecretReference>();
        foreach (var (key, secret) in declaration.SecretEnvironment)
        {
            var address = new ResourceAddress("Secret", secret.Secret);
            if (PreviewReference(references, address) is not { } found)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));
            secrets.Add(key, new FunctionSecretReference(found.ResourceId, secret.Key));
        }

        var spec = FunctionDeclarationJson.Spec(declaration, secrets.ToImmutable(), current.Latest?.Spec.Image);
        var placement = current.Placements.Items[0];
        var projected = Project(
            DeclarationPhase.Configure,
            new FunctionProjectionState(
                root.ResourceGroupId,
                placement.IsolationScopeId,
                root.Placement.DeploymentTargetId,
                root.FunctionRoute,
                placement.Compute,
                current.Latest?.Spec,
                root.Tags));
        var desired = Project(
            DeclarationPhase.Configure,
            new FunctionProjectionState(
                groupId,
                scopeId,
                targetId,
                declaration.FunctionRoute,
                compute.Compute.ToFunctionResourceDeploymentConfiguration(spec.Resources),
                spec,
                declaration.Tags));
        if (references.All(item => item.State == GitPreviewReferenceState.Existing) &&
            JsonElement.DeepEquals(projected.Identity, desired.Identity))
        {
            var checkedPlan = await planning.ReadPlanningWithReferencesAsync(
                new FunctionPlanningInput(
                    request.TenantId,
                    request.ResourceId,
                    request.Declaration,
                    DeclarationPhase.Configure,
                    address => Task.FromResult(PreviewReference(references, address)?.ResourceId)),
                session,
                cancellationToken);
            if (checkedPlan is DeclarationReadBlocked blocked)
                return blocked;
            var dependencies = (FunctionPlanningDependencies)checkedPlan.Value;
            desired = desired with { Dependencies = new DeclarationVersionToken(DependencyVersions(dependencies)) };
        }

        var lifecycle = await lifecycleReader.ReadLifecycleStateAsync(root, session, cancellationToken);
        return new GitLifecycleAdoptionPreview(
            new CurrentDeclaration(
                root.Id,
                new DeclarationVersionToken(LifecycleVersions(lifecycle)),
                projected,
                root.DesiredState == FunctionAppDesiredState.Prepared
                    ? DeclarationConfigurationState.Prepared
                    : DeclarationConfigurationState.Configured),
            desired,
            additional);
    }
}
