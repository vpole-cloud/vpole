using System.Collections.Concurrent;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

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
                "apps.test.local")),
        Revision: 1,
        Enabled: true,
        AcceptsWorkloads: true,
        DeletionRequested: false,
        HealthStatus: "Healthy");

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

    public Task<Result<
        DeploymentTargetLookupDto,
        DeploymentTargetLookupError>> LookupAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_targets.TryGetValue(deploymentTargetId, out var target)
            ? (Result<DeploymentTargetLookupDto, DeploymentTargetLookupError>)
                new Ok<DeploymentTargetLookupDto>(target)
            : new Err<DeploymentTargetLookupError>(
                new DeploymentTargetLookupError(
                    new DeploymentTargetNotFound(deploymentTargetId))));

    public Task<Result<
        DeploymentTargetLookupDto,
        DefaultDeploymentTargetLookupError>> LookupDefaultAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<
            DeploymentTargetLookupDto,
            DefaultDeploymentTargetLookupError>>(
            new Ok<DeploymentTargetLookupDto>(_targets[Target.Id]));

    public async Task<Result<
        DeploymentTargetLookupDto,
        DefaultDeploymentTargetWorkloadAdmissionError>> AdmitDefaultForNewWorkloadAsync(
        CancellationToken cancellationToken = default)
    {
        var admitted = await AdmitForWorkloadAsync(Target.Id, cancellationToken);
        return admitted switch
        {
            Ok<DeploymentTargetLookupDto> target => new Ok<DeploymentTargetLookupDto>(target.Value),
            Err<DeploymentTargetWorkloadAdmissionError> error => error.Value switch
            {
                DeploymentTargetDisabled disabled =>
                    new Err<DefaultDeploymentTargetWorkloadAdmissionError>(disabled),
                DeploymentTargetDeletionRequested deleting =>
                    new Err<DefaultDeploymentTargetWorkloadAdmissionError>(deleting),
                DeploymentTargetNewWorkloadsDisabled unavailable =>
                    new Err<DefaultDeploymentTargetWorkloadAdmissionError>(unavailable),
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

    public Task<Result<
        DeploymentTargetLookupDto,
        DeploymentTargetWorkloadAdmissionError>> AdmitForWorkloadAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        Result<DeploymentTargetLookupDto, DeploymentTargetWorkloadAdmissionError> result;
        if (!_targets.TryGetValue(deploymentTargetId, out var target))
        {
            result = new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetNotFound(deploymentTargetId)));
        }
        else if (!target.Enabled)
        {
            result = new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetDisabled(target.Id)));
        }
        else if (target.DeletionRequested)
        {
            result = new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetDeletionRequested(target.Id)));
        }
        else if (RejectNewWorkloads || !target.AcceptsWorkloads)
        {
            result = new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetNewWorkloadsDisabled(target.Id)));
        }
        else
        {
            result = new Ok<DeploymentTargetLookupDto>(target);
        }

        return Task.FromResult(result);
    }

    public async Task<Result<
        DeploymentTargetLookupDto,
        DeploymentTargetNewWorkloadAdmissionError>> AdmitForWorkloadPlacementAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.ExplicitDeploymentTargetId))
        {
            var explicitResult = await AdmitForWorkloadAsync(
                request.ExplicitDeploymentTargetId,
                cancellationToken);
            if (!explicitResult.TryGetValue(out var target, out var error))
            {
                return new Err<DeploymentTargetNewWorkloadAdmissionError>(
                    new DeploymentTargetNewWorkloadAdmissionError(error));
            }

            var hasRequiredCapabilities =
                !request.RequiredCapabilities.HasFlag(
                    DeploymentTargetRequiredCapabilities.PublicIngress) ||
                target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null;
            return hasRequiredCapabilities
                ? new Ok<DeploymentTargetLookupDto>(target)
                : new Err<DeploymentTargetNewWorkloadAdmissionError>(
                    new DeploymentTargetNewWorkloadAdmissionError(
                        new DeploymentTargetRequiredCapabilitiesUnavailable(
                            target.Id,
                            request.RequiredCapabilities)));
        }

        var eligible = RejectNewWorkloads
            ? []
            : _targets.Values
                .Where(target =>
                    !request.RequiredCapabilities.HasFlag(
                        DeploymentTargetRequiredCapabilities.PublicIngress) ||
                    target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null)
                .OrderBy(target => target.Id, StringComparer.Ordinal)
                .ToArray();
        return eligible.Length switch
        {
            0 => new Err<DeploymentTargetNewWorkloadAdmissionError>(
                new DeploymentTargetNewWorkloadAdmissionError(
                    new NoDeploymentTargetAcceptsWorkloads())),
            > 1 => new Err<DeploymentTargetNewWorkloadAdmissionError>(
                new DeploymentTargetNewWorkloadAdmissionError(
                    new DeploymentTargetWorkloadSelectionRequired(
                        eligible.Select(target => target.Id).ToArray()))),
            _ => new Ok<DeploymentTargetLookupDto>(eligible[0])
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
        else
        {
            result = new DeploymentTargetPublicIngressDto(
                target.Id,
                target.WorkloadConfiguration.Revision,
                target.WorkloadConfiguration.Ingress!.IngressClassName,
                target.WorkloadConfiguration.Ingress.ClusterIssuerName,
                target.WorkloadConfiguration.Ingress.PublicBaseDomain!);
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
                    .Where(target => target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null)
                    .OrderBy(target => target.Id, StringComparer.Ordinal)
                    .Select(target => new DeploymentTargetPublicIngressDto(
                        target.Id,
                        target.WorkloadConfiguration.Revision,
                        target.WorkloadConfiguration.Ingress!.IngressClassName,
                        target.WorkloadConfiguration.Ingress.ClusterIssuerName,
                        target.WorkloadConfiguration.Ingress.PublicBaseDomain!))
                    .ToArray());

    public Task<Result<
        DeploymentTargetPublicIngressDto,
        DeploymentTargetDomainReservationAdmissionError>> AdmitForDomainReservationAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        Result<
            DeploymentTargetPublicIngressDto,
            DeploymentTargetDomainReservationAdmissionError> result;
        if (RejectNewWorkloads)
        {
            result = new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetNewWorkloadsDisabled(deploymentTargetId));
        }
        else if (!_targets.TryGetValue(deploymentTargetId, out var target))
        {
            result = new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetNotFound(deploymentTargetId));
        }
        else if (target.WorkloadConfiguration.Ingress?.PublicBaseDomain is null)
        {
            result = new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetPublicIngressUnavailable(deploymentTargetId));
        }
        else
        {
            result = new Ok<DeploymentTargetPublicIngressDto>(
                new DeploymentTargetPublicIngressDto(
                    target.Id,
                    target.WorkloadConfiguration.Revision,
                    target.WorkloadConfiguration.Ingress!.IngressClassName,
                    target.WorkloadConfiguration.Ingress.ClusterIssuerName,
                    target.WorkloadConfiguration.Ingress.PublicBaseDomain));
        }

        return Task.FromResult(result);
    }

    public Task<Result<Unit, DeploymentTargetReferenceChangeRejected>>
        ApplyChangesOutcomeAsync(
            DeploymentTargetReferenceOwner owner,
            IReadOnlyCollection<string> addedDeploymentTargetIds,
            IReadOnlyCollection<string> removedDeploymentTargetIds,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        if (RejectReferenceRemoval && removedDeploymentTargetIds.Count > 0)
        {
            return Task.FromResult<Result<Unit, DeploymentTargetReferenceChangeRejected>>(
                new Err<DeploymentTargetReferenceChangeRejected>(
                new DeploymentTargetReferenceChangeRejected(
                    owner,
                    addedDeploymentTargetIds
                        .Concat(removedDeploymentTargetIds)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray())));
        }

        _referenceChanges.Enqueue(new DeploymentTargetReferenceChange(
            owner,
            addedDeploymentTargetIds.ToArray(),
            removedDeploymentTargetIds.ToArray()));
        return Task.FromResult<Result<Unit, DeploymentTargetReferenceChangeRejected>>(
            new Ok<Unit>(Unit.Value));
    }

    public Task<Result<
        Unit,
        DeploymentTargetWorkloadConfigurationFenceRejected>> FenceManyOutcomeAsync(
        IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var accepted = requests.All(request =>
            _targets.TryGetValue(request.DeploymentTargetId, out var target) &&
            request.ExpectedWorkloadConfigurationRevision ==
            target.WorkloadConfiguration.Revision);
        return Task.FromResult<Result<
            Unit,
            DeploymentTargetWorkloadConfigurationFenceRejected>>(
            accepted
                ? new Ok<Unit>(Unit.Value)
                : new Err<DeploymentTargetWorkloadConfigurationFenceRejected>(
                    new DeploymentTargetWorkloadConfigurationFenceRejected(requests)));
    }
}

public sealed record DeploymentTargetReferenceChange(
    DeploymentTargetReferenceOwner Owner,
    IReadOnlyList<string> AddedDeploymentTargetIds,
    IReadOnlyList<string> RemovedDeploymentTargetIds);
