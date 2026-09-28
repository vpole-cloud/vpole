using System.Collections.Concurrent;
using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Testing;

public sealed class TestDeploymentTargetLookup
{
    private int _rejectNewWorkloads;
    private int _rejectReferenceRemoval;
    private readonly Dictionary<string, DeploymentTargetLookupDto> _targets =
        new(StringComparer.Ordinal)
        {
            [Target.Id] = Target
        };
    private readonly ConcurrentQueue<DeploymentTargetReferenceChange> _referenceChanges = [];

    public IReadOnlyCollection<DeploymentTargetReferenceChange> ReferenceChanges =>
        _referenceChanges.ToArray();

    private static readonly DeploymentTargetLookupDto Target = new(
        TestPlatformDefaults.DeploymentTargetId,
        "Integration test cluster",
        "test:kubernetes:integration",
        "InCluster",
        ConnectionVersion: 1,
        new DeploymentTargetWorkloadConfigurationLookupDto(
            Revision: 1,
            new DeploymentTargetIngressConfigurationLookupDto(
                "traefik",
                "letsencrypt-prod",
                "apps.test.local"),
            new DeploymentTargetNetworkSourcesDto(
                ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty
                .Add(DeploymentTargetNetworkSourcesDto.PublicIngressName, new DeploymentTargetPodNetworkSourceDto(
                    "kube-system",
                    ImmutableDictionary<string, string>.Empty.Add("app.kubernetes.io/name", "traefik")))
                .Add("functions/gateway", new DeploymentTargetCidrNetworkSourceDto("192.0.2.9/32"))
                .Add("postgres/operator", new DeploymentTargetPodNetworkSourceDto(
                    "cnpg-system",
                    ImmutableDictionary<string, string>.Empty.Add("app.kubernetes.io/name", "cloudnative-pg"))))),
        Revision: 1,
        Enabled: true,
        AcceptsWorkloads: true,
        DeletionRequested: false,
        HealthStatus: "Healthy")
    {
        NetworkPolicy = new(
            NetworkPolicyEnforcementStatus.Supported,
            NetworkPolicyReadiness.EnforcementAttested,
            NetworkPolicyReason.OperatorAttestedSupported,
            DateTime.UnixEpoch,
            NetworkPolicyEvidenceSource.OperatorAttestation,
            false)
    };

    public bool RejectNewWorkloads
    {
        get => Volatile.Read(ref _rejectNewWorkloads) == 1;
        set => Volatile.Write(ref _rejectNewWorkloads, value ? 1 : 0);
    }

    public bool RejectReferenceRemoval
    {
        get => Volatile.Read(ref _rejectReferenceRemoval) == 1;
        set => Volatile.Write(ref _rejectReferenceRemoval, value ? 1 : 0);
    }

    public void AddEligibleTarget(string id, string publicBaseDomain)
    {
        _targets[id] = Target with
        {
            Id = id,
            DisplayName = $"Integration target {id}",
            ClusterIdentity = $"test:kubernetes:{id}",
            WorkloadConfiguration = Target.WorkloadConfiguration with
            {
                Ingress = Target.WorkloadConfiguration.Ingress! with
                {
                    PublicBaseDomain = publicBaseDomain
                }
            }
        };
    }

    public void AddIneligiblePublicIngressTarget(string id, string publicBaseDomain)
    {
        AddEligibleTarget(id, publicBaseDomain);
        var target = _targets[id];
        _targets[id] = target with
        {
            WorkloadConfiguration = target.WorkloadConfiguration with
            {
                NetworkSources = target.WorkloadConfiguration.NetworkSources with
                {
                    Entries = target.WorkloadConfiguration.NetworkSources.Entries.Remove(
                        DeploymentTargetNetworkSourcesDto.PublicIngressName)
                }
            }
        };
    }

    public Task<LookupDeploymentTargetOutcome> LookupAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_targets.TryGetValue(deploymentTargetId, out var target)
            ? (LookupDeploymentTargetOutcome)target
            : new DeploymentTargetLookupError(
                new DeploymentTargetNotFound(deploymentTargetId)));

    public Task<LookupDefaultDeploymentTargetOutcome> LookupDefaultAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<LookupDefaultDeploymentTargetOutcome>(_targets[Target.Id]);

    public async Task<AdmitDefaultDeploymentTargetForNewWorkloadOutcome> AdmitDefaultForNewWorkloadAsync(
        CancellationToken cancellationToken = default)
    {
        var admitted = await AdmitForWorkloadAsync(Target.Id, cancellationToken);
        return admitted switch
        {
            DeploymentTargetLookupDto target => target,
            DeploymentTargetWorkloadAdmissionError error => error.Value switch
            {
                DeploymentTargetDisabled disabled =>
                    new DefaultDeploymentTargetWorkloadAdmissionError(disabled),
                DeploymentTargetDeletionRequested deleting =>
                    new DefaultDeploymentTargetWorkloadAdmissionError(deleting),
                DeploymentTargetNewWorkloadsDisabled unavailable =>
                    new DefaultDeploymentTargetWorkloadAdmissionError(unavailable),
                DeploymentTargetNotFound notFound => throw new InvalidOperationException(
                    $"Configured default deployment target '{notFound.DeploymentTargetId}' does not exist."),
                _ => throw new InvalidOperationException(
                    "Test deployment-target admission returned an unsupported error case.")
            },
            _ => throw new InvalidOperationException(
                "Test deployment-target admission returned an unsupported result case.")
        };
    }

    public Task<IReadOnlyList<DeploymentTargetLookupDto>> ListEligibleAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeploymentTargetLookupDto>>(
            RejectNewWorkloads
                ? []
                : _targets.Values.OrderBy(target => target.Id, StringComparer.Ordinal).ToArray());

    public Task<AdmitDeploymentTargetForWorkloadOutcome> AdmitForWorkloadAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        AdmitDeploymentTargetForWorkloadOutcome result;
        if (!_targets.TryGetValue(deploymentTargetId, out var target))
        {
            result = new DeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetNotFound(deploymentTargetId));
        }
        else if (!target.Enabled)
        {
            result = new DeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetDisabled(target.Id));
        }
        else if (target.DeletionRequested)
        {
            result = new DeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetDeletionRequested(target.Id));
        }
        else if (RejectNewWorkloads || !target.AcceptsWorkloads)
        {
            result = new DeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetNewWorkloadsDisabled(target.Id));
        }
        else
        {
            result = target;
        }

        return Task.FromResult(result);
    }

    public async Task<AdmitDeploymentTargetForNewWorkloadOutcome> AdmitForWorkloadPlacementAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.ExplicitDeploymentTargetId))
        {
            var explicitResult = await AdmitForWorkloadAsync(
                request.ExplicitDeploymentTargetId,
                cancellationToken);
            if (explicitResult is DeploymentTargetWorkloadAdmissionError error)
            {
                return new DeploymentTargetNewWorkloadAdmissionError(error);
            }
            var target = (DeploymentTargetLookupDto)explicitResult.Value;

            var hasRequiredCapabilities =
                (!request.RequiredCapabilities.HasFlag(
                    DeploymentTargetRequiredCapabilities.PublicIngress) ||
                 target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null &&
                 target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName) is not null) &&
                (request.RequiredNetworkSource is null ||
                 target.WorkloadConfiguration.NetworkSources.Get(request.RequiredNetworkSource) is not null);
            return hasRequiredCapabilities
                ? target
                : new DeploymentTargetNewWorkloadAdmissionError(
                        new DeploymentTargetRequiredCapabilitiesUnavailable(
                            target.Id,
                            request.RequiredCapabilities));
        }

        var eligible = RejectNewWorkloads
            ? []
            : _targets.Values
                .Where(target =>
                    !request.RequiredCapabilities.HasFlag(
                        DeploymentTargetRequiredCapabilities.PublicIngress) ||
                    target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null &&
                    target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName) is not null)
                .Where(target => request.RequiredNetworkSource is null ||
                    target.WorkloadConfiguration.NetworkSources.Get(request.RequiredNetworkSource) is not null)
                .OrderBy(target => target.Id, StringComparer.Ordinal)
                .ToArray();
        return eligible.Length switch
        {
            0 => new DeploymentTargetNewWorkloadAdmissionError(
                    new NoDeploymentTargetAcceptsWorkloads()),
            > 1 => new DeploymentTargetNewWorkloadAdmissionError(
                    new DeploymentTargetWorkloadSelectionRequired(
                        eligible.Select(target => target.Id).ToArray())),
            _ => eligible[0]
        };
    }

    public Task<DeploymentTargetPublicIngressAdmissionResult>
        AdmitForNewWorkloadAsync(
            DeploymentTargetAdmissionRequest request,
            CancellationToken cancellationToken = default)
    {
        DeploymentTargetPublicIngressAdmissionResult result;
        if (RejectNewWorkloads)
        {
            result = new DeploymentTargetNewWorkloadsDisabled(
                request.ExplicitDeploymentTargetId ?? string.Empty);
        }
        else if (!_targets.TryGetValue(
                     request.ExplicitDeploymentTargetId ?? string.Empty,
                     out var target))
        {
            result = new DeploymentTargetNotFound(
                request.ExplicitDeploymentTargetId ?? string.Empty);
        }
        else if (target.WorkloadConfiguration.Ingress?.PublicBaseDomain is null ||
                 target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName) is null)
        {
            result = new DeploymentTargetPublicIngressUnavailable(target.Id);
        }
        else
        {
            result = new DeploymentTargetPublicIngressDto(
                target.Id,
                target.WorkloadConfiguration.Revision,
                target.WorkloadConfiguration.Ingress!.IngressClassName,
                target.WorkloadConfiguration.Ingress.ClusterIssuerName,
                target.WorkloadConfiguration.Ingress.PublicBaseDomain!,
                target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName)!);
        }

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<DeploymentTargetPublicIngressDto>>
        ListEligiblePublicIngressForNewWorkloadAsync(
            DeploymentTargetAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeploymentTargetPublicIngressDto>>(
            RejectNewWorkloads
                ? []
                : _targets.Values
                    .Where(target => target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null &&
                                     target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName) is not null)
                    .OrderBy(target => target.Id, StringComparer.Ordinal)
                    .Select(target => new DeploymentTargetPublicIngressDto(
                        target.Id,
                        target.WorkloadConfiguration.Revision,
                        target.WorkloadConfiguration.Ingress!.IngressClassName,
                        target.WorkloadConfiguration.Ingress.ClusterIssuerName,
                        target.WorkloadConfiguration.Ingress.PublicBaseDomain!,
                        target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName)!))
                    .ToArray());

    public Task<AdmitDeploymentTargetPublicIngressForDomainReservationOutcome> AdmitForDomainReservationAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        AdmitDeploymentTargetPublicIngressForDomainReservationOutcome result;
        if (RejectNewWorkloads)
        {
            result = new DeploymentTargetDomainReservationAdmissionError(
                new DeploymentTargetNewWorkloadsDisabled(deploymentTargetId));
        }
        else if (!_targets.TryGetValue(deploymentTargetId, out var target))
        {
            result = new DeploymentTargetDomainReservationAdmissionError(
                new DeploymentTargetNotFound(deploymentTargetId));
        }
        else if (target.WorkloadConfiguration.Ingress?.PublicBaseDomain is null ||
                 target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName) is null)
        {
            result = new DeploymentTargetDomainReservationAdmissionError(
                new DeploymentTargetPublicIngressUnavailable(deploymentTargetId));
        }
        else
        {
            result = new DeploymentTargetPublicIngressDto(
                    target.Id,
                    target.WorkloadConfiguration.Revision,
                    target.WorkloadConfiguration.Ingress!.IngressClassName,
                    target.WorkloadConfiguration.Ingress.ClusterIssuerName,
                    target.WorkloadConfiguration.Ingress.PublicBaseDomain,
                    target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName)!);
        }

        return Task.FromResult(result);
    }

    public Task<ApplyDeploymentTargetReferenceChangesOutcome>
        ApplyChangesOutcomeAsync(
            DeploymentTargetReferenceOwner owner,
            IReadOnlyCollection<string> addedDeploymentTargetIds,
            IReadOnlyCollection<string> removedDeploymentTargetIds,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        if (RejectReferenceRemoval && removedDeploymentTargetIds.Count > 0)
        {
            return Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
                new DeploymentTargetReferenceChangeRejected(
                    owner,
                    addedDeploymentTargetIds
                        .Concat(removedDeploymentTargetIds)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray()));
        }

        _referenceChanges.Enqueue(new DeploymentTargetReferenceChange(
            owner,
            addedDeploymentTargetIds.ToArray(),
            removedDeploymentTargetIds.ToArray()));
        return Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
            new DeploymentTargetReferencesChanged());
    }

    public Task<FenceDeploymentTargetWorkloadConfigurationsOutcome> FenceManyOutcomeAsync(
        IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var accepted = requests.All(request =>
            _targets.TryGetValue(request.DeploymentTargetId, out var target) &&
            request.ExpectedWorkloadConfigurationRevision ==
            target.WorkloadConfiguration.Revision);
        return Task.FromResult<FenceDeploymentTargetWorkloadConfigurationsOutcome>(
            accepted
                ? new DeploymentTargetWorkloadConfigurationsFenced()
                : new DeploymentTargetWorkloadConfigurationFenceRejected(requests));
    }
}

public sealed record DeploymentTargetReferenceChange(
    DeploymentTargetReferenceOwner Owner,
    IReadOnlyList<string> AddedDeploymentTargetIds,
    IReadOnlyList<string> RemovedDeploymentTargetIds);
