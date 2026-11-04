using Pole.Features.GitOps.Primitives;
using System.Text.RegularExpressions;
using static Pole.Features.WebServices.Application.Persistence.WebServiceDeclarationRules;
using Pole.Workloads;
using Pole.Database;
using System.Collections.Immutable;
using System.Text.Json;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceConnections.Contracts;
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
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

public sealed record PinnedComputeProfile(
    string Name,
    long Revision);

public sealed record WebServiceDeclarationPlacement(
    string DeploymentTarget,
    PinnedComputeProfile ComputeProfile,
    string? IsolationScope = null);

public sealed record DeclaredSecretEnvironmentReference(
    string Secret,
    string Key);

public sealed record DeclaredConnectionEnvironmentReference(
    string Connection,
    string ValueKind,
    string? ConfigurationKey = null);

public sealed record WebServiceConnectionReferenceRejected : IDeclarationFailure;

public sealed record WebServiceDeclaration
{
    public required string ResourceGroup { get; init; }

    public required DeclaredWebServiceImage Image { get; init; }

    public required WebServiceDeclarationPlacement Placement { get; init; }

    public required ImmutableList<PortMappingDto> Ports { get; init; }

    public required ProbeSpecDto Probes { get; init; }

    public string Exposure { get; init; } = PrivateExposure;

    public int? Replicas { get; init; }

    public AutoscalingSpecDto? Autoscaling { get; init; }

    internal int FixedReplicas => Replicas ?? Autoscaling?.MinReplicas ?? DefaultReplicas;

    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;

    public ImmutableDictionary<string,
        DeclaredSecretEnvironmentReference> SecretEnvironment
    { get; init; } = ImmutableDictionary<string,
        DeclaredSecretEnvironmentReference>.Empty;

    public ImmutableDictionary<string, DeclaredConnectionEnvironmentReference>? ConnectionEnvironment { get; init; }

    public string[] Tags { get; init; } = [];
}

internal sealed partial class DeclarativeWebServiceProvider(
    IMongoCollection<WebService> services,
    IGitOpsMutations git,
    MongoRunTransaction planningTransactions,
    LoadIsolationScopePlanningSnapshot loadPlanningScope,
    FenceIsolationScopePlanningSnapshot fencePlanningScope,
    LoadResourceDeploymentPlanningSnapshot loadPlanningPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlanningPlacements,
    ResolveComputeProfilePlanningSnapshot resolvePlanningCompute,
    FenceComputeProfilePlanningSnapshot fencePlanningCompute,
    ResolveDeploymentTargetPlanningSnapshot resolvePlanningTarget,
    FenceDeploymentTargetPlanningSnapshot fencePlanningTarget,
    LoadSourceBuildConfigurationReference loadBuildConfiguration,
    FenceSourceBuildConfiguration fenceBuildConfiguration,
    LoadResourceConnectionReferencePlanning loadPlanningConnections,
    FenceResourceConnectionReferencePlanning fencePlanningConnections,
    LoadConsumerSecretPlanningSnapshot loadPlanningSecrets,
    FenceConsumerSecretPlanningSnapshot fencePlanningSecrets,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveProfileName,
    IQuotaAdmissionExecutor quotas,
    IQuotaUsageRelease releaseQuota,
    IGitLifecyclePlanGuard lifecycleGuard,
    LoadResourceConnectionCascadePlanning loadCascadePlanning,
    ApplyWebServiceDeletion deleteWorkflow,
    IResourceRegistrar registrar,
    CreateResourceDeploymentSet createDeployments,
    RequestResourceDeploymentSetApply applyDeployments,
    AdmitConsumerSecretReferences admitSecrets,
    PersistCreatedWebService persistCreated,
    PersistUpdatedWebServiceOutcome persistUpdated,
    PersistCurrentWebServiceRelease persistRelease,
    GetOrCreateGeneratedDomainZones getOrCreateGeneratedDomainZones,
    LoadHostnameBindingsForDeploymentInTransaction loadHostnameBindingsForDeployment,
    CreateGeneratedHostnameBindings createGeneratedHostnameBindings,
    TimeProvider clock) : ITransactionalDeclarativeResourceProvider, IDeclarativeReferencePreparation,
    IDeclarativeResourceLifecycleProvider, IDeclarativeRegisteredResourceProvider,
    IDeclarativeLifecycleDependencyProvider, IDeclarativeAdoptionPreviewProvider,
    IDeclarativeResourceAdoptionProvider, IDeclarativePreviewReferenceProvider
{
    public string Kind => "WebService";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        WebServiceConnectionReferenceRejected => nameof(WebServiceConnectionReferenceRejected),
        WebServiceLifecycleBuildInProgress => nameof(WebServiceLifecycleBuildInProgress),
        WebServiceLifecycleDeploymentRejected => nameof(WebServiceLifecycleDeploymentRejected),
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
            WebServiceDeclarationError.PublicIngressUnavailable => "PublicIngressUnavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public int MaximumNameLength => WebServiceDeclarationRules.MaximumNameLength;

    private static readonly Regex EnvironmentName = new("^[A-Za-z_][A-Za-z0-9_]*$");

    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<WebServiceDeclaration>(spec);

        return new(new("ResourceGroup", value.ResourceGroup),
            [],
            [.. value.Placement.IsolationScope is null ? Array.Empty<DeclaredReference>() :
                [new(new("IsolationScope", value.Placement.IsolationScope), "spec.placement.isolationScope")],
            .. value.Image is DeclaredSourceBuildImage build ? new DeclaredReference[]
                { new(new("SourceBuildConfiguration", build.SourceBuildConfiguration), "spec.image") } : [],
            .. (value.ConnectionEnvironment ?? []).Select(pair => new DeclaredReference(
                new("ResourceConnection", pair.Value.Connection), $"spec.connectionEnvironment.{pair.Key}")),
            .. value.SecretEnvironment.Select(pair =>
                new DeclaredReference(
                    new("Secret", pair.Value.Secret),
                    $"spec.secretEnvironment.{pair.Key}",
                    pair.Value.Key,
                    RequiresAccess: true))],
            [],
            []);
    }

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<WebServiceDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup), "spec.resourceGroup");
        DeclarationJson.Require(value.Image switch
        {
            DeclaredContainerImage image =>
                image.Server is { Length: > 0 and <= MaximumImageComponentLength } &&
                image.Name is { Length: > 0 and <= MaximumImageComponentLength } &&
                image.Tag is { Length: > 0 and <= MaximumImageTagLength } &&
                !image.Server.Any(char.IsWhiteSpace) && !image.Name.Any(char.IsWhiteSpace) &&
                !image.Tag.Any(char.IsWhiteSpace),
            DeclaredSourceBuildImage image => DeclarationNames.Valid(image.SourceBuildConfiguration),
            null => false
        }, "spec.image");
        DeclarationJson.Require(value.Placement is not null
                                && ResourceTechnicalName.IsValid(value.Placement.DeploymentTarget) &&
                                value.Placement.ComputeProfile is not null
                                && ResourceTechnicalName.IsValid(value.Placement.ComputeProfile.Name) &&
                                value.Placement.ComputeProfile.Revision > 0,
            "spec.placement");
        DeclarationJson.Require(value.Placement.IsolationScope is null ||
                                ResourceTechnicalName.IsValid(value.Placement.IsolationScope) &&
                                value.Placement.IsolationScope != "default", "spec.placement.isolationScope");
        DeclarationJson.Require(value.Exposure is PrivateExposure or PublicExposure, "spec.exposure");
        DeclarationJson.Require(value.Replicas is null || value.Autoscaling is null, "spec.replicas");
        DeclarationJson.Require(value.Autoscaling is null || ValidAutoscaling(value.Autoscaling), "spec.autoscaling");
        DeclarationJson.Require(
            value.FixedReplicas is >= DefaultReplicas and <= MaximumReplicas && value.Tags is not null
                                                                        && value.Tags.All(tag =>
                                                                            tag is
                                                                            {
                                                                                Length: > 0 and <= MaximumTagLength
                                                                            } && !string.IsNullOrWhiteSpace(tag)),
            "spec.replicas");
        DeclarationJson.Require(value.Ports is { Count: > 0 and <= MaximumPorts } && value.Ports.All(port =>
                                    port is not null &&
                                    port.ServicePort is >= MinimumPort and <= MaximumPort
                                    && port.TargetPort is { Length: > 0 and <= MaximumPortTextLength } && int.TryParse(
                                        port.TargetPort,
                                        out var number) && number is >= MinimumPort and <= MaximumPort &&
                                    port.Protocol is TcpProtocol or UdpProtocol && port.Name is
                                    { Length: <= MaximumPortNameLength }
                                    && DeclarationNames.Valid(port.Name)) &&
                                value.Ports.Select(port => port.Name).Distinct().Count() == value.Ports.Count &&
                                value.Ports.Select(port => (port.ServicePort, port.Protocol)).Distinct()
                                    .Count() == value.Ports.Count,
            "spec.ports");
        DeclarationJson.Require(value.Exposure != PublicExposure ||
                                value.Ports.OrderBy(port => port.Name, StringComparer.Ordinal).First().Protocol ==
                                TcpProtocol, "spec.ports");
        DeclarationJson.Require(value.Probes is not null && ValidProbe(value.Probes.Startup)
                                                         && value.Probes.Startup.SuccessThreshold ==
                                                         RequiredLivenessSuccessThreshold &&
                                                         ValidProbe(value.Probes.Liveness)
                                                         && value.Probes.Liveness.SuccessThreshold ==
                                                         RequiredLivenessSuccessThreshold
                                                         && ValidProbe(value.Probes.Readiness),
            "spec.probes");
        DeclarationJson.Require(value.Environment is not null && value.SecretEnvironment is not null &&
                                value.Environment.Count + value.SecretEnvironment.Count <= MaximumEnvironmentEntries
                                && value.Environment.Values.All(item => item is not null) &&
                                value.Environment.Keys.Concat(value.SecretEnvironment.Keys)
                                    .All(name =>
                                        name.Length <= MaximumEnvironmentNameLength && EnvironmentName.IsMatch(name)) &&
                                !value.Environment.Keys.Intersect(value.SecretEnvironment.Keys).Any() &&
                                value.SecretEnvironment.Values.All(reference =>
                                    reference is not null && DeclarationNames.Valid(reference.Secret) &&
                                    SecretKeyReferenceRules.ValidKey(reference.Key)),
            "spec.environment");

        var connections = value.ConnectionEnvironment ?? [];
        DeclarationJson.Require(connections.Count + value.Environment.Count + value.SecretEnvironment.Count <=
            MaximumEnvironmentEntries && connections.All(pair =>
                pair.Key.Length <= MaximumEnvironmentNameLength && EnvironmentName.IsMatch(pair.Key) &&
                pair.Value is not null && DeclarationNames.Valid(pair.Value.Connection) &&
                (pair.Value.ValueKind is "Host" or "Port" && pair.Value.ConfigurationKey is null ||
                 pair.Value.ValueKind == "Configuration" && !string.IsNullOrWhiteSpace(pair.Value.ConfigurationKey))) &&
            !connections.Keys.Intersect(value.Environment.Keys.Concat(value.SecretEnvironment.Keys)).Any(),
            "spec.connectionEnvironment");

        return DeclarationJson.Write(value with
        {
            Replicas = value.Autoscaling is null ? value.FixedReplicas : null,
            Ports = value.Ports.OrderBy(port => port.Name, StringComparer.Ordinal).ToImmutableList(),
            Tags = value.Tags.Select(tag => tag.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray()
        });
    }

    private static bool ValidAutoscaling(AutoscalingSpecDto value) =>
        value.MinReplicas is >= DefaultReplicas and <= MaximumReplicas &&
        value.MaxReplicas >= value.MinReplicas && value.MaxReplicas <= MaximumReplicas &&
        value.Metric is { Type: "cpu" or "memory", TargetAverageUtilization: >= 1 };

    private static bool ValidProbe(ProbeConfigDto? probe) =>
        probe is not null && probe.Path is { Length: > 0 and <= MaximumProbePathLength }
                          && probe.Path.StartsWith('/') &&
                          probe.Port is { Length: > 0 and <= MaximumPortTextLength } && int.TryParse(
                              probe.Port,
                              out var port) && port is >= MinimumPort and <= MaximumPort
                          && probe.InitialDelaySeconds is >= 0 and <= MaximumProbeInitialDelaySeconds &&
                          probe.PeriodSeconds is >= 1 and <= MaximumProbePeriodSeconds
                          && probe.TimeoutSeconds is >= 1 and <= MaximumProbeTimeoutSeconds
                          && probe.FailureThreshold is >= 1 and <= MaximumProbeThreshold
                          && probe.SuccessThreshold is >= 1 and <= MaximumProbeThreshold;

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<WebServiceDeclaration>(spec);

        return
        [
            new(
                DeclarationPhase.Prepare,
                [
                    new(new("ResourceGroup", value.ResourceGroup), DeclarationPhase.Prepare),
                    .. value.Placement.IsolationScope is null ? Array.Empty<DeclarationDependency>() :
                        [new(new("IsolationScope", value.Placement.IsolationScope), DeclarationPhase.Prepare)]
                ]),
            new(DeclarationPhase.Configure,
            [
                new(address, DeclarationPhase.Prepare),
                .. value.Image is DeclaredSourceBuildImage build ? new DeclarationDependency[]
                    { new(new("SourceBuildConfiguration", build.SourceBuildConfiguration), DeclarationPhase.Configure) }
                    : [],
                .. (value.ConnectionEnvironment ?? []).Values.Select(reference => new DeclarationDependency(
                    new("ResourceConnection", reference.Connection), DeclarationPhase.Bind)),
                .. value.SecretEnvironment.Values.Select(reference => new DeclarationDependency(
                    new("Secret", reference.Secret),
                    DeclarationPhase.Prepare))
            ])
        ];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await services.Find(
            session,
            WebServiceFilters.ByTechnicalName(request.TenantId, request.Name)).FirstOrDefaultAsync(ct))?.Id;

    public async Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct)
    {
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var context = operation.Context;
        var loaded = await planningTransactions.InvokeOutcomeAsync<ReadWebServicePlanningOutcome>(async (tx, token) =>
            tx.Commit(await ReadExecutionPlanningAsync(operation.Input, tx.Session, token)), ct);
        if (loaded is DeclarationReadBlocked blocked)
            return await BlockAsync(operation, blocked.Failure, ct);

        var dependencies = (WebServicePlanningDependencies)loaded.Value;
        var current = dependencies.Current;
        var deployments = dependencies.Placements?.Items ?? [];
        var compute = dependencies.Compute.Compute;
        var selectedTarget = dependencies.Target.Target;
        var binding = new WebServiceDeclaredPlacement(spec.Placement.DeploymentTarget, selectedTarget.Id,
            spec.Placement.ComputeProfile.Name, compute.ComputeProfileId);
        var domainSpec = ToSpec(spec, ImmutableDictionary<string, SecretEnvironmentReference>.Empty,
            current?.Spec, dependencies.ConnectionReferences, dependencies.BuildConfigurationId);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInput([(compute, spec.FixedReplicas)], domainSpec);
        QuotaMutation<WebServiceQuotaInput> quotaChange = current is null
            ? new CreateQuotaMutation<WebServiceQuotaInput>(nextQuota)
            : new UpdateQuotaMutation<WebServiceQuotaInput>(
                WebServiceQuotaInput.Create(deployments.Select(item => WebServiceDeploymentQuotaInput.Create(
                    item.Compute.Requests.CpuMillis, item.Compute.Requests.MemoryMi,
                    current.Spec.Autoscaling?.MaxReplicas ?? item.FixedReplicas))), nextQuota);
        var outcome = await quotas.ExecuteOutcomeAsync<WebServiceQuotaInput, GitOperationResult>(
            new ExecuteQuotaAdmissionRequest<global::Pole.Features.WebServices.Application.Common
                .WebServiceQuotaInput, global::Pole.Features.GitOps.Contracts.GitOperationResult>(
                TenantId: context.TenantId,
                ResourceKey: WebServiceQuotaResourceKey.Create(operation.ResourceId),
                Mutation: quotaChange,
                QuotaError: _ => new(
                    operation.ResourceId,
                    new WebServiceDeclarationRejected(WebServiceDeclarationError.QuotaUnavailable)),
                MutationCallback: async (tx, token) =>
                {
                    var rejected = await git.BeginAsync(operation, tx.Session, token);

                    if (rejected is not null)
                        return tx.Abort(rejected);

                    var groupId = await git.ResolveAcceptedAsync(
                        new ResolveAcceptedGitReferenceRequest(
                            Context: context,
                            ConsumerResourceId: operation.ResourceId,
                            Address: new("ResourceGroup", spec.ResourceGroup)),
                        session: tx.Session,
                        ct: token);

                    if (groupId is null)
                        return tx.Abort(new(
                            operation.ResourceId,
                            new WebServiceDeclarationRejected(
                                WebServiceDeclarationError.ResourceGroupUnavailable)));

                    GitOperationResult result;

                    if (current is null && await services.Find(
                            tx.Session,
                            WebServiceFilters.ById(context.TenantId, operation.ResourceId)).AnyAsync(token))
                        return tx.Abort(new(
                            operation.ResourceId,
                            new DeclarationPreconditionsChanged()));

                    if (current is null)
                        result = await PrepareAsync(
                            operation,
                            spec,
                            domainSpec,
                            dependencies.Scope,
                            compute!,
                            selectedTarget!,
                            binding,
                            tx.Session,
                            token);
                    else if (groupId != current.ResourceGroupId ||
                             dependencies.Scope.IsolationScopeId != current.IsolationScopeId)
                        return tx.Abort(new(
                            operation.ResourceId,
                            new WebServiceDeclarationRejected(
                                WebServiceDeclarationError.ImmutablePlacementChange)));
                    else
                        result = await ConfigureAsync(
                            operation,
                            spec,
                            current,
                            dependencies,
                            compute,
                            binding,
                            tx.Session,
                            token);

                    if (!result.Applied)
                        return tx.Abort(result);

                    var completed = await git.CompleteAsync(
                        new CompleteGitOperationRequest(
                            Operation: operation,
                            Result: result),
                        session: tx.Session,
                        ct: token);

                    return completed.Applied ? tx.Commit(completed) : tx.Abort(completed);
                }),
            cancellationToken: ct);

        return outcome.Applied || outcome.Failure is DeclarationPreconditionsChanged or StaleGitExecution
            ? outcome : await BlockAsync(operation, outcome.Failure!, ct);
    }

    private Task<GitOperationResult> BlockAsync(
        GitOperation operation,
        IDeclarationFailure failure,
        CancellationToken ct) =>
        git.ExecuteAsync(
            operation,
            (_, _) => Task.FromResult(new GitOperationResult(operation.ResourceId, failure)),
            ct);

    private async Task<GitOperationResult> PrepareAsync(GitOperation operation,
        WebServiceDeclaration spec,
        WebServiceSpec domainSpec,
        IsolationScopePlanningSnapshot scope,
        ResolvedComputeProfile compute,
        DeploymentTargetLookupDto target,
        WebServiceDeclaredPlacement binding,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var groupId = scope.ResourceGroupId;

        var service = WebServiceFactory.Create(operation.Declaration.Address.Name,
            operation.Context.TenantId,
            groupId,
            scope.IsolationScopeId,
            domainSpec,
            WebServiceExposure.Create(),
            WebServiceFactory.DefaultOptions with
            {
                Id = operation.ResourceId,
                Tags = spec.Tags.ToImmutableHashSet(),
                Lifecycle = new PreparedWebServiceLifecycle()
            }).BindDeclaredPlacement(binding);

        if (await registrar.RegisterAsync(
                new RegisterResourceRequest(
                    TenantId: service.TenantId,
                    ResourceGroupId: groupId,
                    Resource: new(WebServiceResourceDeployment.ResourceType, service.Id, service.Name)),
                session: session,
                cancellationToken: ct) is ResourceRegistrationMutationError)
            return new(
                service.Id,
                new WebServiceDeclarationRejected(WebServiceDeclarationError.ResourceRegistrationUnavailable));

        var created = await createDeployments(new([
                new(ObjectId.GenerateNewId().ToString(),
                    service.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    service.Id,
                    groupId,
                    service.IsolationScopeId,
                    new(
                        target.Id,
                        target.WorkloadConfiguration.Revision,
                        null),
                    compute.ToResourceDeploymentConfiguration(),
                    null,
                    ResourcePlacementCapability.MultiTarget,
                    spec.FixedReplicas,
                    service.ActiveRevision,
                    DeferredActivation: true,
                    PinnedCompute: true)
            ]),
            session,
            ct);

        if (created is not CreatedResourceDeployments)
            return new(service.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.PlacementUnavailable));

        var persisted = await persistCreated(session, service, service.ToCreateEvent([]), ct);

        if (persisted.Value is WebServiceNameAlreadyExists)
            return new(
                service.Id,
                new WebServiceDeclarationRejected(WebServiceDeclarationError.ExplicitAdoptionRequired));

        return new(service.Id, Changed: true);
    }

    private async Task<GitOperationResult> ConfigureAsync(GitOperation operation,
        WebServiceDeclaration spec,
        WebService current,
        WebServicePlanningDependencies dependencies,
        ResolvedComputeProfile compute,
        WebServiceDeclaredPlacement binding,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var authoritative = await services.Find(
            session,
            WebServiceFilters.ById(current.TenantId, current.Id)).FirstOrDefaultAsync(ct);

        if (authoritative is null || authoritative.Revision != current.Revision)
            return new(
                current.Id,
                new DeclarationPreconditionsChanged());

        current = authoritative;
        var placements = dependencies.Placements!.Items;
        var references = ImmutableDictionary.CreateBuilder<string, SecretEnvironmentReference>();
        foreach (var pair in spec.SecretEnvironment)
        {
            var secretId = await git.ResolveAcceptedAsync(
                new ResolveAcceptedGitReferenceRequest(
                    Context: operation.Context,
                    ConsumerResourceId: operation.ResourceId,
                    Address: new("Secret", pair.Value.Secret)),
                session: session,
                ct: ct);

            if (secretId is null)
                return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.SecretUnavailable));

            references.Add(pair.Key, new(secretId, pair.Value.Key));
        }

        var desired = ToSpec(spec, references.ToImmutable(), current.Spec, dependencies.ConnectionReferences,
            dependencies.BuildConfigurationId);
        var admitted = await admitSecrets(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: current.TenantId,
                ResourceGroupId: current.ResourceGroupId,
                IsolationScopeId: current.IsolationScopeId,
                ConsumerType: WebServiceResourceDeployment.ResourceType,
                ConsumerId: current.Id,
                PreviousReferences: current.Spec.Config.SecretEnvironmentReferences.Values.Select(reference =>
                    new ConsumerSecretKeyReference(
                        reference.SecretId,
                        reference.Key)).ToArray(),
                DesiredReferences: desired.Config.SecretEnvironmentReferences.Values.Select(reference =>
                    new ConsumerSecretKeyReference(
                        reference.SecretId,
                        reference.Key)).ToArray(),
                Availability: ConsumerSecretAvailabilityPolicy.RequireAvailable,
                Retention: PreviousSecretReferenceRetention.KeepUntilRolloutConfirmed),
            session: session,
            ct: ct);

        if (admitted is ConsumerSecretReferenceUnavailable unavailable)
            return new(current.Id, new WebServiceSecretReferenceRejected(unavailable));

        var desiredExposure = current.Exposure with
        { Mode = spec.Exposure == PublicExposure ? WebServiceExposureMode.Public : WebServiceExposureMode.Private };
        var awaiting = desired.Image is PendingSourceBuildImage;
        var next = (awaiting
            ? current.AcceptConfigurationAwaitingArtifact(desired, spec.Tags, desiredExposure)
            : current.ActivatePrepared(desired, spec.Tags, desiredExposure)).BindDeclaredPlacement(binding);
        var replacementCompute = compute.ToResourceDeploymentConfiguration();
        var placementChanged = placements.Any(item => item.FixedReplicas != spec.FixedReplicas ||
            item.State == ResourceDeploymentPlanningState.Prepared && !awaiting ||
            IngressProjection(item.Ingress) != DesiredIngress(spec, dependencies.Target.Target) ||
            item.Compute.ComputeProfileId != replacementCompute.ComputeProfileId ||
            item.Compute.ComputeProfileRevision != replacementCompute.ComputeProfileRevision ||
            !JsonElement.DeepEquals(ComputeProjection(item.Compute), ComputeProjection(replacementCompute)));

        if (next == current && !placementChanged)
            return new(current.Id);

        if (next == current)
            next = current.AdvancePlacementDesiredState();

        var exposure = await PrepareIngressAsync(new(next, dependencies.Target.Target, placements), session, ct);
        if (exposure is WebServiceDeclarationRejected rejected)
            return new(current.Id, rejected);
        var ingress = ((PreparedWebServiceIngress)exposure.Value).Changes;

        var applied = await applyDeployments(new(current.TenantId,
                WebServiceResourceDeployment.ResourceType,
                current.Id,
                next.ActiveRevision,
                placements.Select(item => new ResourceDeploymentSetApplyItem(item.DeploymentId,
                    item.Revision,
                    new(item.Compute.ComputeProfileId, item.Compute.ComputeProfileRevision),
                    ingress[item.DeploymentId],
                    replacementCompute,
                    spec.FixedReplicas,
                    PinnedCompute: true)).ToArray(),
                awaiting ? ResourceDeploymentActivation.KeepPrepared : ResourceDeploymentActivation.Reconcile),
            session,
            ct);

        if (applied is ResourceDeploymentMutationError
            {
                Value: ResourceDeploymentChanged or
                ResourceDeploymentComputeConfigurationChanged or ResourceDeploymentIngressConfigurationChanged
            })
            return new(current.Id, new DeclarationPreconditionsChanged());

        if (applied is not MutatedResourceDeployments updated)
            return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.PlacementChanged));

        var persisted = await persistUpdated(
            session,
            current,
            next,
            next.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(updated.Placements)),
            ct,
            operation.Context);

        if (persisted is WebServiceDependencyUnavailable dependency)
            return new(current.Id, new WebServiceDependencyRejected(dependency));

        if (persisted is WebServiceUpdateConflict conflict)
            return new(current.Id, conflict.Reason == WebServiceUpdateConflictReason.ExpectedRevisionConflict
                ? new DeclarationPreconditionsChanged() : new GitDeclarationOwnershipConflict());

        if (persisted is WebServiceConnectionConflict)
            return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.PlacementChanged));

        if (awaiting)
            return new(current.Id, Changed: true);

        var eventCore = EventCore.Create(current.TenantId);
        var release = WebServiceReleaseFactory.CreatePending(current.TenantId,
            current.Id,
            next.ActiveRevision,
            current.Lifecycle is PreparedWebServiceLifecycle
                ? WebServiceReleaseAction.Create
                : WebServiceReleaseAction.Update,
            null,
            null,
            next.Spec,
            WebServiceDeploymentWorkflow.ToReleaseDeployments(updated.Placements),
            eventCore.TraceId,
            eventCore.CorrelationId,
            new(ObjectId.GenerateNewId().ToString(), clock.GetUtcNow().UtcDateTime),
            next.Exposure,
            new GitOpsReleaseProvenance(operation.Context.SourceId,
                operation.Context.RunId,
                operation.Context.Authority.Commit,
                current.Spec.Image.ResolvedImage?.Tag,
                next.Spec.Image.ResolvedImage!.Tag));
        await persistRelease(session, release, ct);

        return new(current.Id, Changed: true);
    }

    private static WebServiceSpec ToSpec(
        WebServiceDeclaration value,
        ImmutableDictionary<string, SecretEnvironmentReference> references,
        WebServiceSpec? previous,
        ImmutableDictionary<string, ConnectionEnvironmentReference>? connections = null,
        string? buildConfigurationId = null) =>
        new(value.Image switch
        {
            DeclaredContainerImage image => new ImageInfo(image.Server, image.Name, image.Tag,
                previous?.Image.ResolvedImage?.SecretName),
            DeclaredSourceBuildImage when previous?.Image is { } prior &&
                prior.SourceBuildConfigurationId == buildConfigurationId &&
                prior is PendingSourceBuildImage or DeliveredSourceBuildImage => prior,
            DeclaredSourceBuildImage => WebServiceImageFactory.Pending(buildConfigurationId!)
        },
            new WebServiceConfig()
            {
                EnvironmentVariables = value.Environment,
                PortMappings = value.Ports.Select(port => new PortMapping(
                    port.ServicePort,
                    port.TargetPort,
                    port.Protocol,
                    port.Name)).ToImmutableList(),
                ConnectionEnvironmentReferences = connections ?? previous?.Config.ConnectionEnvironmentReferences ??
                    ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty,
                SecretEnvironmentReferences = references
            },
            value.Autoscaling?.ToDomain(),
            value.Probes.ToDomain());
}
