using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Workloads;

namespace Pole.Features.Functions.Application.Persistence;

internal static class FunctionDeclarations
{
    internal const string Kind = "FunctionApp";
    internal static ResourceTypeId ResourceType => FunctionResourceDeployment.ResourceType;
    internal static string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        FunctionLifecycleDeploymentRejected => nameof(FunctionLifecycleDeploymentRejected),
        FunctionDeclarationSecretRejected rejected => ConsumerSecretReferenceErrorCodes.Code(rejected.Reason),
        FunctionDeclarationRejected rejected => rejected.Reason switch
        {
            FunctionDeclarationError.AcceptedSnapshotRequired =>
                nameof(FunctionDeclarationError.AcceptedSnapshotRequired),
            FunctionDeclarationError.ResourceDeleting => nameof(FunctionDeclarationError.ResourceDeleting),
            FunctionDeclarationError.ResourceGroupUnavailable =>
                nameof(FunctionDeclarationError.ResourceGroupUnavailable),
            FunctionDeclarationError.IsolationScopeUnavailable =>
                nameof(FunctionDeclarationError.IsolationScopeUnavailable),
            FunctionDeclarationError.DeploymentTargetUnavailable =>
                nameof(FunctionDeclarationError.DeploymentTargetUnavailable),
            FunctionDeclarationError.ComputeProfileUnavailable =>
                nameof(FunctionDeclarationError.ComputeProfileUnavailable),
            FunctionDeclarationError.PlacementUnavailable => nameof(FunctionDeclarationError.PlacementUnavailable),
            FunctionDeclarationError.ResourceRegistrationUnavailable =>
                nameof(FunctionDeclarationError.ResourceRegistrationUnavailable),
            FunctionDeclarationError.QuotaUnavailable => nameof(FunctionDeclarationError.QuotaUnavailable),
            FunctionDeclarationError.ExplicitAdoptionRequired =>
                nameof(FunctionDeclarationError.ExplicitAdoptionRequired),
            FunctionDeclarationError.UnexpectedOperation => nameof(FunctionDeclarationError.UnexpectedOperation),
            FunctionDeclarationError.FunctionRouteUnavailable =>
                nameof(FunctionDeclarationError.FunctionRouteUnavailable),
            FunctionDeclarationError.ConfigurationUnavailable =>
                nameof(FunctionDeclarationError.ConfigurationUnavailable),
            FunctionDeclarationError.PendingRevision => nameof(FunctionDeclarationError.PendingRevision),
            FunctionDeclarationError.SecretReferenceUnavailable =>
                nameof(FunctionDeclarationError.SecretReferenceUnavailable),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    internal static JsonElement NormalizeAndValidate(JsonElement spec) => FunctionDeclarationJson.Normalize(spec);

    internal static DeclarationFacts Facts(JsonElement spec)
    {
        var value = FunctionDeclarationJson.Read(spec);
        return new(new("ResourceGroup", value.ResourceGroup), [],
        [
            .. value.Placement.IsolationScope is { } scope
                ? new DeclaredReference[] { new(new("IsolationScope", scope), "spec.placement.isolationScope") } : [],
            .. value.SecretEnvironment.Select(pair => new DeclaredReference(new("Secret", pair.Value.Secret),
                $"spec.secretEnvironment.{pair.Key}", pair.Value.Key, RequiresAccess: true))
        ], [], []);
    }

    internal static IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        DeclarationJson.Require(address.Name.Length >= 3, "metadata.name");
        var value = FunctionDeclarationJson.Read(spec);
        return
        [
            new(DeclarationPhase.Prepare,
            [
                new(new("ResourceGroup", value.ResourceGroup), DeclarationPhase.Prepare),
                .. value.Placement.IsolationScope is { } scope
                    ? new DeclarationDependency[] { new(new("IsolationScope", scope), DeclarationPhase.Prepare) } : []
            ]),
            new(DeclarationPhase.Configure,
            [
                new(address, DeclarationPhase.Prepare),
                .. value.SecretEnvironment.Values.Select(reference => new DeclarationDependency(
                    new("Secret", reference.Secret), DeclarationPhase.Prepare))
            ])
        ];
    }



    internal static DeclarationProjection Project(DeclarationPhase phase, FunctionProjectionState state) => new(
        DeclarationJson.Write(new
        {
            state.ResourceGroupId,
            state.IsolationScopeId,
            state.DeploymentTargetId,
            state.FunctionRoute
        }),
        phase == DeclarationPhase.Prepare || state.Spec is null
            ? DeclarationJson.Write(new { })
            : DeclarationJson.Write(new
            {
                tags = state.Tags.Order(StringComparer.Ordinal),
                configuration = ManagedSpec(state.Spec),
                state.Compute.ComputeProfileId,
                state.Compute.ComputeProfileRevision,
                compute = ComputeProjection(state.Compute)
            }));

    internal static JsonElement ManagedSpec(FunctionRevisionSpec spec) => DeclarationJson.Write(new
    {
        image = new { spec.Image.Server, spec.Image.Name, spec.Image.Tag },
        environment = spec.Configuration.EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        secretEnvironment = spec.Configuration.SecretReferences.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        spec.Resources,
        spec.Scaling,
        handlers = spec.Handlers.OrderBy(handler => handler.Name, StringComparer.Ordinal).Select(handler => new
        {
            handler.Name,
            handler.MaxConcurrencyPerInstance,
            trigger = handler.Trigger switch
            {
                HttpFunctionTriggerSpec http => DeclarationJson.Write(new
                {
                    type = "http",
                    http.Route,
                    methods = http.Methods.Select(method => method.ToUpperInvariant()).Order(StringComparer.Ordinal),
                    exposure = http.Exposure.ToString()
                }),
                TimerFunctionTriggerSpec timer => DeclarationJson.Write(new
                {
                    type = "timer",
                    timer.Cron,
                    concurrencyPolicy = timer.ConcurrencyPolicy.ToString(),
                    misfirePolicy = timer.MisfirePolicy.ToString(),
                    timer.Retry
                }),
                _ => throw new ArgumentOutOfRangeException(nameof(handler))
            }
        })
    });

    internal static string ManagedSpecHash(FunctionRevisionSpec spec) => Hash(ManagedSpec(spec));

    internal static string PlanningRootHash(FunctionApp app) => Hash(DeclarationJson.Write(new
    {
        app.Name,
        app.NormalizedName,
        app.ResourceGroupId,
        app.Placement,
        app.FunctionRoute,
        app.DesiredState,
        app.AdmittedQuota,
        tags = app.Tags.Order(StringComparer.Ordinal)
    }));

    internal static string Hash(JsonElement value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));


    internal static JsonElement ComputeProjection(ResourceDeploymentComputeConfiguration compute) =>
        DeclarationJson.Write(new
        {
            compute.Requests,
            compute.Limits,
            compute.Architecture,
            compute.RuntimeClassName,
            extendedResources = compute.ExtendedResources.OrderBy(item => item.Name, StringComparer.Ordinal),
            nodeSelector = compute.NodeSelector.OrderBy(item => item.Key, StringComparer.Ordinal),
            requiredNodeAffinity = compute.RequiredNodeAffinity.Select(NormalizeTerm)
                .OrderBy(item => DeclarationJson.Write(item).GetRawText(), StringComparer.Ordinal),
            preferredNodeAffinity = compute.PreferredNodeAffinity.Select(item =>
                    item with { Term = NormalizeTerm(item.Term) })
                .OrderBy(item => DeclarationJson.Write(item).GetRawText(), StringComparer.Ordinal),
            tolerations = compute.Tolerations.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ThenBy(item => item.Operator).ThenBy(item => item.Value, StringComparer.Ordinal)
                .ThenBy(item => item.Effect).ThenBy(item => item.TolerationSeconds)
        });

    internal static ResourceDeploymentNodeAffinityTerm NormalizeTerm(ResourceDeploymentNodeAffinityTerm term) =>
        term with
        {
            Expressions = term.Expressions.Select(item => item with
            { Values = item.Values.Order(StringComparer.Ordinal).ToImmutableList() })
                .OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Operator).ToImmutableList()
        };

    internal static ConsumerSecretKeyReference[] PreviousSecrets(FunctionPlanningCurrent? current) =>
        current is null ? [] : ConfigurationRevisions(current).SelectMany(revision =>
                revision.Spec.Configuration.SecretReferences.Values)
            .Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key))
            .Distinct().ToArray();

    internal static IEnumerable<FunctionRevision> ConfigurationRevisions(FunctionPlanningCurrent current) =>
        new[] { current.Latest, current.Active }.OfType<FunctionRevision>().DistinctBy(revision => revision.Id);

    internal static DeclarationDocumentVersion[] CurrentVersions(FunctionPlanningCurrent current) =>
    [
        new($"FunctionApp/{current.Root.Id}", current.Root.NextRevisionNumber),
        new($"FunctionApp/{current.Root.Id}/PlanningFence", current.Root.PlanningFence),
        new($"FunctionApp/{current.Root.Id}/Managed/{PlanningRootHash(current.Root)}", 0),
        new($"FunctionApp/{current.Root.Id}/Pointers/{current.Root.ActiveRevisionId}/{current.Root.PendingRevisionId}",
            0),
        new($"FunctionRoute/{current.Route.NormalizedRoute}/{current.Route.State}/{current.Route.ActiveRevisionId}",
            current.Route.PlanningFence),
        .. ConfigurationRevisions(current).Select(revision => new DeclarationDocumentVersion(
            $"FunctionRevision/{revision.Id}/{ManagedSpecHash(revision.Spec)}", revision.PlanningFence)),
        .. current.Placements.Items.Select(item =>
            new DeclarationDocumentVersion($"ResourceDeployment/{item.DeploymentId}", item.Revision))
    ];

    internal static DeclarationDocumentVersion[] DependencyVersions(FunctionPlanningDependencies dependencies) =>
    [
        new($"ResourceGroup/{dependencies.Scope.ResourceGroupId}", dependencies.Scope.ResourceGroupRevision),
        new($"IsolationScope/{dependencies.Scope.IsolationScopeId}", dependencies.Scope.ScopeRevision),
        new($"DeploymentTarget/{dependencies.Target.Target.Id}", dependencies.Target.Target.Revision),
        new($"DeploymentTarget/{dependencies.Target.Target.Id}/WorkloadConfiguration",
            dependencies.Target.Target.WorkloadConfiguration.Revision),
        new($"ComputeProfile/{dependencies.Compute.Compute.ComputeProfileId}/Policy",
            dependencies.Compute.PolicyRevision),
        .. dependencies.Secrets?.Secrets.SelectMany(version => new DeclarationDocumentVersion[]
        {
            new($"Secret/{version.SecretId}", version.Revision),
            new($"Secret/{version.SecretId}/ReferenceFence", version.ReferenceFence)
        }) ?? []
    ];

    internal static DeclarationDocumentVersion[] LifecycleVersions(FunctionLifecycleState state) =>
    [
        new($"FunctionApp/{state.Root.Id}", state.Root.NextRevisionNumber),
        new($"FunctionApp/{state.Root.Id}/PlanningFence", state.Root.PlanningFence),
        new($"FunctionApp/{state.Root.Id}/Managed/{PlanningRootHash(state.Root)}", 0),
        new($"FunctionApp/{state.Root.Id}/Placements/{LifecyclePlacementHash(state.Placements)}", 0),
        new($"FunctionApp/{state.Root.Id}/Pointers/{state.Root.ActiveRevisionId}/{state.Root.PendingRevisionId}", 0),
        new($"FunctionRoute/{state.Root.FunctionRoute}/{state.Route?.State}/{state.Route?.ActiveRevisionId}",
            state.Route?.PlanningFence ?? 0),
        .. state.Configuration.Select(item => new DeclarationDocumentVersion(
            $"FunctionRevision/{item.Id}/{ManagedSpecHash(item.Spec)}", item.PlanningFence)),
        .. state.Placements.SelectMany(item => LifecyclePlacementVersions(item,
            state.Root.DesiredState == FunctionAppDesiredState.Deleting))
    ];

    internal static string LifecyclePlacementHash(IReadOnlyList<ResourceDeploymentPlanningItem> placements) =>
        Hash(DeclarationJson.Write(placements.OrderBy(item => item.DeploymentId, StringComparer.Ordinal)
            .Select(item => new
            {
                item.DeploymentId,
                item.ResourceGroupId,
                item.IsolationScopeId,
                item.DeploymentTargetId,
                item.FixedReplicas,
                item.State,
                compute = ComputeProjection(item.Compute)
            })));

    internal static IReadOnlyList<DeclarationDocumentVersion> LifecyclePlacementVersions(
        ResourceDeploymentPlanningItem item,
        bool deleting)
    {
        if (!deleting || item.State != ResourceDeploymentPlanningState.Deleting)
            return [new($"ResourceDeployment/{item.DeploymentId}/DesiredGeneration", item.DesiredGeneration),
                new($"ResourceDeployment/{item.DeploymentId}/ResourceRevision", item.ResourceRevision)];
        var intent = new
        {
            item.ResourceGroupId,
            item.IsolationScopeId,
            item.DeploymentTargetId,
            item.FixedReplicas,
            item.Compute.ComputeProfileId,
            item.Compute.ComputeProfileRevision,
            compute = ComputeProjection(item.Compute),
            item.Storage,
            ingress = item.Ingress is { } ingress ? new
            {
                ingress.IngressClassName,
                ingress.ClusterIssuerName,
                ingress.PublicBaseDomain,
                hostnames = ingress.Hostnames.Order(StringComparer.Ordinal)
            } : null,
            item.DeploymentTargetConfigurationRevision,
            item.DesiredGeneration,
            item.ResourceRevision
        };
        var hash = Hash(DeclarationJson.Write(intent));
        return [new($"ResourceDeployment/{item.DeploymentId}/DeleteIntent/{hash}", item.DesiredGeneration)];
    }

    internal static bool SameLifecycleVersions(IEnumerable<DeclarationDocumentVersion> current,
        IEnumerable<DeclarationDocumentVersion> expected) =>
        current.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(item => item.DocumentId, StringComparer.Ordinal));

    internal static GitPreviewResolvedReference? PreviewReference(
        IReadOnlyList<GitPreviewResolvedReference> references, ResourceAddress address) =>
        references.SingleOrDefault(item => item.Address == address);

    internal static DeclarationReadBlocked Blocked(FunctionDeclarationError reason) =>
        new(new FunctionDeclarationRejected(reason));
}

internal sealed record FunctionProjectionState(
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId,
    string FunctionRoute,
    ResourceDeploymentComputeConfiguration Compute,
    FunctionRevisionSpec? Spec,
    IEnumerable<string> Tags);
