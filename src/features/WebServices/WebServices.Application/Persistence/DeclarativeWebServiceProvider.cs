using System.Text.RegularExpressions;
using static Pole.Features.WebServices.Application.Persistence.WebServiceDeclarationRules;
using Pole.Workloads;
using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

public sealed record PinnedComputeProfile(
    string Name,
    long Revision);
public sealed record WebServiceDeclarationPlacement(
    string DeploymentTarget,
    PinnedComputeProfile ComputeProfile);
public sealed record DeclaredSecretEnvironmentReference(
    string Secret,
    string Key);
public sealed record WebServiceDeclaration
{
    public required string ResourceGroup { get; init; }
    public required ImageInfoDto Image { get; init; }
    public required WebServiceDeclarationPlacement Placement { get; init; }
    public required ImmutableList<PortMappingDto> Ports { get; init; }
    public required ProbeSpecDto Probes { get; init; }
    public string Exposure { get; init; } = PrivateExposure;
    public int Replicas { get; init; } = DefaultReplicas;
    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, DeclaredSecretEnvironmentReference> SecretEnvironment { get; init; } = ImmutableDictionary<string, DeclaredSecretEnvironmentReference>.Empty;
    public string[] Tags { get; init; } = [];
}

internal sealed class DeclarativeWebServiceProvider(
    IMongoCollection<WebService> services,
    IGitOpsMutations git,
    GetResourceGroupOwnership groupOwnership,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveProfileName,
    ResolveComputeProfiles resolveCompute,
    IQuotaAdmissionExecutor quotas,
    IResourceRegistrar registrar,
    CreateResourceDeploymentSet createDeployments,
    LoadResourceDeploymentSet loadDeployments,
    RequestResourceDeploymentSetApply applyDeployments,
    AdmitConsumerSecretReferences admitSecrets,
    PersistCreatedWebService persistCreated,
    PersistUpdatedWebServiceOutcome persistUpdated,
    PersistCurrentWebServiceRelease persistRelease,
    TimeProvider clock) : IDeclarativeResourceProvider
{
    public string Kind => "WebService";
    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        WebServiceSecretReferenceRejected rejected => ConsumerSecretReferenceErrorCodes.Code(rejected.Error.Reason),
        WebServiceDependencyRejected rejected => WebServiceDependencyDiagnostics.Code(rejected.Error),
        WebServiceDeclarationRejected rejected => rejected.Reason switch
        {
            WebServiceDeclarationError.ExplicitAdoptionRequired => "ExplicitAdoptionRequired",
            WebServiceDeclarationError.ResourceDeleting => "ResourceDeleting",
            WebServiceDeclarationError.DeploymentTargetNameAmbiguous => "DeploymentTargetNameAmbiguous",
            WebServiceDeclarationError.DeploymentTargetUnavailable => "DeploymentTargetUnavailable",
            WebServiceDeclarationError.PreparedPlacementIdentityUnavailable => "PreparedPlacementIdentityUnavailable",
            WebServiceDeclarationError.ImmutablePlacementChange => "ImmutablePlacementChange",
            WebServiceDeclarationError.PreparedResourceUnavailable => "PreparedResourceUnavailable",
            WebServiceDeclarationError.ComputeProfileNameAmbiguous => "ComputeProfileNameAmbiguous",
            WebServiceDeclarationError.ComputeProfileRevisionUnavailable => "ComputeProfileRevisionUnavailable",
            WebServiceDeclarationError.PinnedComputeProfileUnavailable => "PinnedComputeProfileUnavailable",
            WebServiceDeclarationError.QuotaUnavailable => "QuotaUnavailable",
            WebServiceDeclarationError.ResourceGroupUnavailable => "ResourceGroupUnavailable",
            WebServiceDeclarationError.ExpectedRevisionConflict => "ExpectedRevisionConflict",
            WebServiceDeclarationError.ResourceRegistrationUnavailable => "ResourceRegistrationUnavailable",
            WebServiceDeclarationError.PlacementUnavailable => "PlacementUnavailable",
            WebServiceDeclarationError.PlacementChanged => "PlacementChanged",
            WebServiceDeclarationError.SecretUnavailable => "SecretUnavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };
    public int MaximumNameLength => WebServiceDeclarationRules.MaximumNameLength;
    private static readonly Regex EnvironmentName = new("^[A-Za-z_][A-Za-z0-9_]*$");
    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<WebServiceDeclaration>(spec);
        return new(new("ResourceGroup", value.ResourceGroup), [], value.SecretEnvironment.Select(pair =>
            new DeclaredReference(new("Secret", pair.Value.Secret), $"spec.secretEnvironment.{pair.Key}", pair.Value.Key, RequiresAccess: true)).ToArray(), [], []);
    }
    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<WebServiceDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup) && value.Image is not null &&
            value.Image.Server is { Length: > 0 and <= MaximumImageComponentLength } && value.Image.Name is { Length: > 0 and <= MaximumImageComponentLength } &&
            value.Image.Tag is { Length: > 0 and <= MaximumImageTagLength } && !value.Image.Server.Any(char.IsWhiteSpace) && !value.Image.Name.Any(char.IsWhiteSpace) && !value.Image.Tag.Any(char.IsWhiteSpace), "spec.image");
        DeclarationJson.Require(value.Placement is not null && ResourceTechnicalName.IsValid(value.Placement.DeploymentTarget) &&
            value.Placement.ComputeProfile is not null && ResourceTechnicalName.IsValid(value.Placement.ComputeProfile.Name) &&
            value.Placement.ComputeProfile.Revision > 0, "spec.placement");
        DeclarationJson.Require(value.Exposure == PrivateExposure, "spec.exposure");
        DeclarationJson.Require(value.Replicas is >= DefaultReplicas and <= MaximumReplicas && value.Tags is not null && value.Tags.All(tag => tag is { Length: > 0 and <= MaximumTagLength } && !string.IsNullOrWhiteSpace(tag)), "spec.replicas");
        DeclarationJson.Require(value.Ports is { Count: > 0 and <= MaximumPorts } && value.Ports.All(port => port is not null &&
            port.ServicePort is >= MinimumPort and <= MaximumPort && port.TargetPort is { Length: > 0 and <= MaximumPortTextLength } && int.TryParse(port.TargetPort, out var number) && number is >= MinimumPort and <= MaximumPort &&
            port.Protocol is TcpProtocol or UdpProtocol && port.Name is { Length: <= MaximumPortNameLength } && DeclarationNames.Valid(port.Name)) &&
            value.Ports.Select(port => port.Name).Distinct().Count() == value.Ports.Count &&
            value.Ports.Select(port => (port.ServicePort, port.Protocol)).Distinct().Count() == value.Ports.Count, "spec.ports");
        DeclarationJson.Require(value.Probes is not null && ValidProbe(value.Probes.Startup) && value.Probes.Startup.SuccessThreshold == RequiredLivenessSuccessThreshold &&
            ValidProbe(value.Probes.Liveness) && value.Probes.Liveness.SuccessThreshold == RequiredLivenessSuccessThreshold && ValidProbe(value.Probes.Readiness), "spec.probes");
        DeclarationJson.Require(value.Environment is not null && value.SecretEnvironment is not null &&
            value.Environment.Count + value.SecretEnvironment.Count <= MaximumEnvironmentEntries && value.Environment.Values.All(item => item is not null) &&
            value.Environment.Keys.Concat(value.SecretEnvironment.Keys).All(name => name.Length <= MaximumEnvironmentNameLength && EnvironmentName.IsMatch(name)) &&
            !value.Environment.Keys.Intersect(value.SecretEnvironment.Keys).Any() &&
            value.SecretEnvironment.Values.All(reference => reference is not null && DeclarationNames.Valid(reference.Secret) &&
                SecretKeyReferenceRules.ValidKey(reference.Key)), "spec.environment");
        return DeclarationJson.Write(value with
        {
            Ports = value.Ports.OrderBy(port => port.Name, StringComparer.Ordinal).ToImmutableList(),
            Tags = value.Tags.Select(tag => tag.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray()
        });
    }
    private static bool ValidProbe(ProbeConfigDto? probe) => probe is not null && probe.Path is { Length: > 0 and <= MaximumProbePathLength } && probe.Path.StartsWith('/') &&
        probe.Port is { Length: > 0 and <= MaximumPortTextLength } && int.TryParse(probe.Port, out var port) && port is >= MinimumPort and <= MaximumPort && probe.InitialDelaySeconds is >= 0 and <= MaximumProbeInitialDelaySeconds &&
        probe.PeriodSeconds is >= 1 and <= MaximumProbePeriodSeconds && probe.TimeoutSeconds is >= 1 and <= MaximumProbeTimeoutSeconds && probe.FailureThreshold is >= 1 and <= MaximumProbeThreshold && probe.SuccessThreshold is >= 1 and <= MaximumProbeThreshold;
    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<WebServiceDeclaration>(spec);
        return [new(DeclarationPhase.Prepare, [new(new("ResourceGroup", value.ResourceGroup), DeclarationPhase.Prepare)]),
            new(DeclarationPhase.Configure, [new(address, DeclarationPhase.Prepare),
                .. value.SecretEnvironment.Values.Select(reference => new DeclarationDependency(new("Secret", reference.Secret), DeclarationPhase.Prepare))])];
    }
    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await services.Find(session, WebServiceFilters.ByTechnicalName(request.TenantId, request.Name)).FirstOrDefaultAsync(ct))?.Id;

    public async Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct)
    {
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var context = operation.Context;
        var current = await services.Find(WebServiceFilters.ByTechnicalName(context.TenantId, operation.Declaration.Address.Name)).FirstOrDefaultAsync(ct);
        if (current is not null && current.Id != operation.ResourceId)
            return await BlockAsync(operation, new WebServiceDeclarationRejected(WebServiceDeclarationError.ExplicitAdoptionRequired), ct);
        if (current is not null && current.Lifecycle is DeletingWebServiceLifecycle)
            return await BlockAsync(operation, new WebServiceDeclarationRejected(WebServiceDeclarationError.ResourceDeleting), ct);

        DeploymentTargetLookupDto? selectedTarget = null;
        string targetId;
        if (current is null)
        {
            var target = await resolveTargetName(new(context.TenantId, WebServiceResourceDeployment.ResourceType,
                spec.Placement.DeploymentTarget), ct);
            if (target is not DeploymentTargetLookupDto resolvedTarget)
                return await BlockAsync(operation, target is NamedDeploymentTargetAmbiguous
                    ? new WebServiceDeclarationRejected(WebServiceDeclarationError.DeploymentTargetNameAmbiguous) : new WebServiceDeclarationRejected(WebServiceDeclarationError.DeploymentTargetUnavailable), ct);
            selectedTarget = resolvedTarget;
            targetId = selectedTarget.Id;
        }
        else
        {
            if (current.DeclaredPlacement is null)
                return await BlockAsync(operation, new WebServiceDeclarationRejected(WebServiceDeclarationError.PreparedPlacementIdentityUnavailable), ct);
            if (current.DeclaredPlacement.DeploymentTargetName != spec.Placement.DeploymentTarget)
                return await BlockAsync(operation, new WebServiceDeclarationRejected(WebServiceDeclarationError.ImmutablePlacementChange), ct);
            targetId = current.DeclaredPlacement.DeploymentTargetId;
        }

        var deployments = current is null ? [] : (await loadDeployments(
            new LoadResourceDeploymentSetRequest(
                TenantId: current.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: current.Id),
            cancellationToken: ct)).ToArray();
        if (current is not null && (deployments.Length != 1 || deployments[0].DeploymentTargetId != targetId))
            return await BlockAsync(operation, new WebServiceDeclarationRejected(WebServiceDeclarationError.ImmutablePlacementChange), ct);
        if (operation.Phase == DeclarationPhase.Prepare && current is not null)
            return await git.ExecuteAsync(operation, async (session, token) =>
            {
                var groupId = await git.ResolveAsync(
                    new ResolveGitResourceRequest(
                        TenantId: context.TenantId,
                        Address: new("ResourceGroup", spec.ResourceGroup)),
                    session: session,
                    ct: token);
                return groupId == current.ResourceGroupId ? new(current.Id) : new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.ImmutablePlacementChange));
            }, ct);
        if (operation.Phase == DeclarationPhase.Configure && current is null)
            return await BlockAsync(operation, new WebServiceDeclarationRejected(WebServiceDeclarationError.PreparedResourceUnavailable), ct);

        ResolvedComputeProfile? compute = null;
        var desiredProfile = spec.Placement.ComputeProfile;
        string profileId;
        if (current?.DeclaredPlacement is { } bound && bound.ComputeProfileName == desiredProfile.Name)
            profileId = bound.ComputeProfileId;
        else
        {
            var named = await resolveProfileName(new(context.TenantId, WebServiceResourceDeployment.ResourceType,
                desiredProfile.Name, targetId, desiredProfile.Revision), ct);
            if (named is not ResolvedComputeProfile resolved)
                return await BlockAsync(operation, named switch
                {
                    NamedComputeProfileAmbiguous => new WebServiceDeclarationRejected(WebServiceDeclarationError.ComputeProfileNameAmbiguous),
                    ComputeProfileUnavailable { Reason: ComputeProfileUnavailableReason.RevisionUnavailable } => new WebServiceDeclarationRejected(WebServiceDeclarationError.ComputeProfileRevisionUnavailable),
                    _ => new WebServiceDeclarationRejected(WebServiceDeclarationError.PinnedComputeProfileUnavailable)
                }, ct);
            compute = resolved;
            profileId = compute.ComputeProfileId;
        }
        if (compute is null && (current is null || deployments[0].Compute.Profile.ComputeProfileId != profileId ||
            deployments[0].Compute.Profile.ComputeProfileRevision != desiredProfile.Revision))
        {
            var resolution = await resolveCompute(new(context.TenantId, WebServiceResourceDeployment.ResourceType,
                [new(targetId, profileId, desiredProfile.Revision)]), ct);
            if (resolution is not IReadOnlyList<ResolvedComputeProfile> resolved)
                return await BlockAsync(operation, resolution is ComputeProfileResolutionError error &&
                    error.Value is ComputeProfileUnavailable { Reason: ComputeProfileUnavailableReason.RevisionUnavailable }
                    ? new WebServiceDeclarationRejected(WebServiceDeclarationError.ComputeProfileRevisionUnavailable) : new WebServiceDeclarationRejected(WebServiceDeclarationError.PinnedComputeProfileUnavailable), ct);
            compute = resolved.Single();
        }
        var binding = new WebServiceDeclaredPlacement(spec.Placement.DeploymentTarget, targetId, desiredProfile.Name, profileId);
        var domainSpec = ToSpec(spec, ImmutableDictionary<string, SecretEnvironmentReference>.Empty);
        var nextQuota = compute is not null ? WebServiceDeploymentWorkflow.ToQuotaInput([(compute, spec.Replicas)], domainSpec) :
            WebServiceDeploymentWorkflow.ToQuotaInput(deployments.Select(item => item with { FixedReplicas = spec.Replicas }), domainSpec);
        QuotaMutation<WebServiceQuotaInput> quotaChange = current is null ? new CreateQuotaMutation<WebServiceQuotaInput>(nextQuota) :
            new UpdateQuotaMutation<WebServiceQuotaInput>(WebServiceDeploymentWorkflow.ToQuotaInput(deployments, current.Spec), nextQuota);
        var outcome = await quotas.ExecuteOutcomeAsync<WebServiceQuotaInput, GitOperationResult>(
            new ExecuteQuotaAdmissionRequest<global::Pole.Features.WebServices.Application.Common.WebServiceQuotaInput, global::Pole.Features.GitOps.Contracts.GitOperationResult>(
                TenantId: context.TenantId,
                ResourceKey: WebServiceQuotaResourceKey.Create(operation.ResourceId),
                Mutation: quotaChange,
                QuotaError: _ => new(operation.ResourceId, new WebServiceDeclarationRejected(WebServiceDeclarationError.QuotaUnavailable)),
                MutationCallback: async (tx, token) =>
                {
                    var rejected = await git.BeginAsync(operation, tx.Session, token);
                    if (rejected is not null) return tx.Abort(rejected);
                    var groupId = await git.ResolveAsync(
                        new ResolveGitResourceRequest(
                            TenantId: context.TenantId,
                            Address: new("ResourceGroup", spec.ResourceGroup)),
                        session: tx.Session,
                        ct: token);
                    if (groupId is null) return tx.Abort(new(operation.ResourceId, new WebServiceDeclarationRejected(WebServiceDeclarationError.ResourceGroupUnavailable)));
                    GitOperationResult result;
                    if (current is null && await services.Find(tx.Session, WebServiceFilters.ById(context.TenantId, operation.ResourceId)).AnyAsync(token))
                        return tx.Abort(new(operation.ResourceId, new WebServiceDeclarationRejected(WebServiceDeclarationError.ExpectedRevisionConflict)));
                    if (current is null) result = await PrepareAsync(operation, spec, domainSpec, groupId, compute!, selectedTarget!, binding, tx.Session, token);
                    else if (groupId != current.ResourceGroupId) return tx.Abort(new(operation.ResourceId, new WebServiceDeclarationRejected(WebServiceDeclarationError.ImmutablePlacementChange)));
                    else result = await ConfigureAsync(operation, spec, current, deployments, compute, binding, tx.Session, token);
                    if (!result.Applied) return tx.Abort(result);
                    await git.CompleteAsync(
                        new CompleteGitOperationRequest(
                            Operation: operation,
                            Result: result),
                        session: tx.Session,
                        ct: token);
                    return tx.Commit(result);
                }),
            cancellationToken: ct);
        return outcome.Applied ? outcome : await BlockAsync(operation, outcome.Failure!, ct);
    }
    private Task<GitOperationResult> BlockAsync(GitOperation operation, IDeclarationFailure failure, CancellationToken ct) =>
        git.ExecuteAsync(operation, (_, _) => Task.FromResult(new GitOperationResult(operation.ResourceId, failure)), ct);

    private async Task<GitOperationResult> PrepareAsync(GitOperation operation, WebServiceDeclaration spec, WebServiceSpec domainSpec,
        string groupId, ResolvedComputeProfile compute, DeploymentTargetLookupDto target, WebServiceDeclaredPlacement binding,
        IClientSessionHandle session, CancellationToken ct)
    {
        var group = await groupOwnership(operation.Context.TenantId, groupId, ct);
        if (group is not ResourceGroupOwnership owned) return new(operation.ResourceId, new WebServiceDeclarationRejected(WebServiceDeclarationError.ResourceGroupUnavailable));
        var service = WebServiceFactory.Create(operation.Declaration.Address.Name, operation.Context.TenantId, groupId, owned.DefaultIsolationScopeId,
            domainSpec, WebServiceExposure.Create(), WebServiceFactory.DefaultOptions with { Id = operation.ResourceId, Tags = spec.Tags.ToImmutableHashSet(), Lifecycle = new PreparedWebServiceLifecycle() }).BindDeclaredPlacement(binding);
        if (await registrar.RegisterAsync(
            new RegisterResourceRequest(
                TenantId: service.TenantId,
                ResourceGroupId: groupId,
                Resource: new(WebServiceResourceDeployment.ResourceType, service.Id, service.Name)),
            session: session,
            cancellationToken: ct) is ResourceRegistrationMutationError)
            return new(service.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.ResourceRegistrationUnavailable));
        var created = await createDeployments(new([new(ObjectId.GenerateNewId().ToString(), service.TenantId,
            WebServiceResourceDeployment.ResourceType, service.Id, groupId, service.IsolationScopeId,
            new(target.Id, target.WorkloadConfiguration.Revision, null), compute.ToResourceDeploymentConfiguration(), null,
            ResourcePlacementCapability.MultiTarget, spec.Replicas, service.ActiveRevision, DeferredActivation: true, PinnedCompute: true)]), session, ct);
        if (created is not CreatedResourceDeployments) return new(service.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.PlacementUnavailable));
        var persisted = await persistCreated(session, service, service.ToCreateEvent([]), ct);
        if (persisted.Value is WebServiceNameAlreadyExists) return new(service.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.ExplicitAdoptionRequired));
        return new(service.Id, Changed: true);
    }
    private async Task<GitOperationResult> ConfigureAsync(GitOperation operation, WebServiceDeclaration spec, WebService current,
        IReadOnlyList<PlacementSummaryDto> placements, ResolvedComputeProfile? compute, WebServiceDeclaredPlacement binding,
        IClientSessionHandle session, CancellationToken ct)
    {
        var authoritative = await services.Find(session, WebServiceFilters.ById(current.TenantId, current.Id)).FirstOrDefaultAsync(ct);
        if (authoritative is null || authoritative.Revision != current.Revision) return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.ExpectedRevisionConflict));
        var references = ImmutableDictionary.CreateBuilder<string, SecretEnvironmentReference>();
        foreach (var pair in spec.SecretEnvironment)
        {
            var secretId = await git.ResolveAsync(
                new ResolveGitResourceRequest(
                    TenantId: current.TenantId,
                    Address: new("Secret", pair.Value.Secret)),
                session: session,
                ct: ct);
            if (secretId is null) return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.SecretUnavailable));
            references.Add(pair.Key, new(secretId, pair.Value.Key));
        }
        var desired = ToSpec(spec, references.ToImmutable());
        var admitted = await admitSecrets(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: current.TenantId,
                ResourceGroupId: current.ResourceGroupId,
                IsolationScopeId: current.IsolationScopeId,
                ConsumerType: WebServiceResourceDeployment.ResourceType,
                ConsumerId: current.Id,
                PreviousReferences: current.Spec.Config.SecretEnvironmentReferences.Values.Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key)).ToArray(),
                DesiredReferences: desired.Config.SecretEnvironmentReferences.Values.Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key)).ToArray(),
                Availability: ConsumerSecretAvailabilityPolicy.RequireAvailable,
                Retention: PreviousSecretReferenceRetention.KeepUntilRolloutConfirmed),
            session: session,
            ct: ct);
        if (admitted is ConsumerSecretReferenceUnavailable unavailable) return new(current.Id, new WebServiceSecretReferenceRejected(unavailable));
        var next = current.ActivatePrepared(desired, spec.Tags, WebServiceExposure.Create()).BindDeclaredPlacement(binding);
        var placementChanged = compute is not null || placements.Any(item => item.FixedReplicas != spec.Replicas || item.Status == ResourceDeploymentStatus.Prepared);
        if (next == current && !placementChanged) return new(current.Id);
        if (next == current) next = current.AdvancePlacementDesiredState();
        var applied = await applyDeployments(new(current.TenantId, WebServiceResourceDeployment.ResourceType, current.Id, next.ActiveRevision,
            placements.Select(item => new ResourceDeploymentSetApplyItem(item.DeploymentId, item.DeploymentRevision,
                item.Compute.Profile, new PreserveIngress(), compute?.ToResourceDeploymentConfiguration(), spec.Replicas, PinnedCompute: true)).ToArray()), session, ct);
        if (applied is not MutatedResourceDeployments updated) return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.PlacementChanged));
        var persisted = await persistUpdated(session, current, next, next.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(updated.Placements)), ct, operation.Context);
        if (persisted is WebServiceDependencyUnavailable dependency) return new(current.Id, new WebServiceDependencyRejected(dependency));
        if (persisted is not WebServiceUpdateAccepted) return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.ExpectedRevisionConflict));
        var eventCore = EventCore.Create(current.TenantId);
        var release = WebServiceReleaseFactory.CreatePending(current.TenantId, current.Id, next.ActiveRevision,
            current.Lifecycle is PreparedWebServiceLifecycle ? WebServiceReleaseAction.Create : WebServiceReleaseAction.Update,
            operation.Context.ActorUserId, operation.Context.ActorEmail, next.Spec,
            WebServiceDeploymentWorkflow.ToReleaseDeployments(updated.Placements), eventCore.TraceId, eventCore.CorrelationId,
            new(ObjectId.GenerateNewId().ToString(), clock.GetUtcNow().UtcDateTime), next.Exposure);
        await persistRelease(session, release, ct);
        return new(current.Id, Changed: true);
    }
    private static WebServiceSpec ToSpec(WebServiceDeclaration value, ImmutableDictionary<string, SecretEnvironmentReference> references) =>
        new(new(value.Image.Server, value.Image.Name, value.Image.Tag),
            new WebServiceConfig()
            {
                EnvironmentVariables = value.Environment,
                PortMappings = value.Ports.Select(port => new PortMapping(port.ServicePort, port.TargetPort, port.Protocol, port.Name)).ToImmutableList(),
                ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty,
                SecretEnvironmentReferences = references
            }, null,
            new WebServiceSpecDto(value.Image, new()
            {
                EnvironmentVariables = value.Environment,
                PortMappings = value.Ports,
                ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReferenceDto>.Empty,
                SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReferenceDto>.Empty
            }, null, value.Probes).ToDomain().Probes);
}
