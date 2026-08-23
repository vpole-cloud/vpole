using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

internal sealed class DeploymentTargetAdmissionQueries(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IMongoCollection<DeploymentTargetSettings> settings)
{
    public async Task<Result<
        DeploymentTargetLookupDto,
        DeploymentTargetLookupError>> LookupAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeploymentTargetId = NormalizeDeploymentTargetId(deploymentTargetId);

        var target = await deploymentTargets
            .Find(DeploymentTargetFilters.ById(normalizedDeploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);
        return target is null
            ? new Err<DeploymentTargetLookupError>(
                new DeploymentTargetLookupError(
                    new DeploymentTargetNotFound(normalizedDeploymentTargetId)))
            : new Ok<DeploymentTargetLookupDto>(target.ToLookupDto());
    }

    public async Task<Result<
        DeploymentTargetLookupDto,
        DefaultDeploymentTargetLookupError>> LookupDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        var target = await LoadDefaultTargetAsync(cancellationToken);
        return target switch
        {
            Ok<DeploymentTarget> value => new Ok<DeploymentTargetLookupDto>(value.Value.ToLookupDto()),
            Err<DefaultDeploymentTargetLookupError> error => error,
            _ => throw new InvalidOperationException(
                "Deployment-target lookup returned an unsupported result case.")
        };
    }

    public async Task<Result<
        DeploymentTargetLookupDto,
        DeploymentTargetWorkloadAdmissionError>> AdmitForWorkloadAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeploymentTargetId = NormalizeDeploymentTargetId(deploymentTargetId);

        var target = await deploymentTargets
            .Find(DeploymentTargetFilters.ById(normalizedDeploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetNotFound(normalizedDeploymentTargetId)));
        }

        var lookup = target.ToLookupDto();
        if (!lookup.Enabled)
        {
            return new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetDisabled(lookup.Id)));
        }

        if (lookup.DeletionRequested)
        {
            return new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetDeletionRequested(lookup.Id)));
        }

        if (!lookup.AcceptsWorkloads)
        {
            return new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetNewWorkloadsDisabled(lookup.Id)));
        }

        return new Ok<DeploymentTargetLookupDto>(lookup);
    }

    public async Task<Result<
        DeploymentTargetLookupDto,
        DefaultDeploymentTargetWorkloadAdmissionError>> AdmitDefaultForNewWorkloadAsync(
        CancellationToken cancellationToken = default)
    {
        var target = await LoadDefaultTargetAsync(cancellationToken);
        return target switch
        {
            Ok<DeploymentTarget> targetSuccess => AdmitForNewWorkload(targetSuccess.Value.ToLookupDto()),
            Err<DefaultDeploymentTargetLookupError> lookupError => lookupError.Value switch
            {
                DefaultDeploymentTargetNotConfigured notConfigured =>
                    new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                        new DefaultDeploymentTargetWorkloadAdmissionError(notConfigured))
            }
        };
    }

    private static Result<
        DeploymentTargetLookupDto,
        DefaultDeploymentTargetWorkloadAdmissionError> AdmitForNewWorkload(
        DeploymentTargetLookupDto lookup)
    {
        if (!lookup.Enabled)
        {
            return new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetDisabled(lookup.Id));
        }

        if (lookup.DeletionRequested)
        {
            return new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetDeletionRequested(lookup.Id));
        }

        if (!lookup.AcceptsWorkloads)
        {
            return new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetNewWorkloadsDisabled(lookup.Id));
        }

        return new Ok<DeploymentTargetLookupDto>(lookup);
    }

    public async Task<IReadOnlyList<DeploymentTargetLookupDto>> ListEligibleAsync(
        CancellationToken cancellationToken = default) =>
        (await deploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken))
        .Select(target => target.ToLookupDto())
        .ToArray();

    public async Task<Result<
        DeploymentTargetLookupDto,
        DeploymentTargetNewWorkloadAdmissionError>> AdmitForWorkloadPlacementAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.ExplicitDeploymentTargetId))
        {
            var resolved = await AdmitForWorkloadAsync(
                request.ExplicitDeploymentTargetId,
                cancellationToken);
            if (!resolved.TryGetValue(out var target, out var admissionError))
            {
                return new Err<DeploymentTargetNewWorkloadAdmissionError>(
                    new DeploymentTargetNewWorkloadAdmissionError(admissionError));
            }

            return HasRequiredCapabilities(target, request.RequiredCapabilities)
                ? new Ok<DeploymentTargetLookupDto>(target)
                : new Err<DeploymentTargetNewWorkloadAdmissionError>(
                    new DeploymentTargetNewWorkloadAdmissionError(
                        new DeploymentTargetRequiredCapabilitiesUnavailable(
                            target.Id,
                            request.RequiredCapabilities)));
        }

        var targets = await deploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken);
        var eligible = targets
            .Select(target => target.ToLookupDto())
            .Where(target => HasRequiredCapabilities(
                target,
                request.RequiredCapabilities))
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

    public async Task<DeploymentTargetPublicIngressAdmissionResult>
        AdmitForNewWorkloadAsync(
            DeploymentTargetAdmissionRequest request,
            CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.ExplicitDeploymentTargetId))
        {
            var deploymentTargetId = NormalizeDeploymentTargetId(
                request.ExplicitDeploymentTargetId);

            var target = await deploymentTargets
                .Find(DeploymentTargetFilters.ById(deploymentTargetId))
                .FirstOrDefaultAsync(cancellationToken);
            if (target is null)
                return new DeploymentTargetNotFound(deploymentTargetId);

            var lookup = target.ToLookupDto();
            if (!lookup.Enabled)
            {
                return new DeploymentTargetDisabled(lookup.Id);
            }

            if (lookup.DeletionRequested)
            {
                return new DeploymentTargetDeletionRequested(lookup.Id);
            }

            if (!lookup.AcceptsWorkloads)
            {
                return new DeploymentTargetNewWorkloadsDisabled(lookup.Id);
            }

            return HasRequiredCapabilities(
                    lookup,
                    DeploymentTargetRequiredCapabilities.PublicIngress)
                ? ToPublicIngressDto(lookup)
                : new DeploymentTargetPublicIngressUnavailable(lookup.Id);
        }

        var targets = await deploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken);
        var eligible = targets
            .Select(target => target.ToLookupDto())
            .Where(target => HasRequiredCapabilities(
                target,
                DeploymentTargetRequiredCapabilities.PublicIngress))
            .ToArray();

        return eligible.Length switch
        {
            0 => new NoDeploymentTargetAcceptsPublicWorkloads(),
            > 1 => new DeploymentTargetSelectionRequired(
                eligible.Select(target => target.Id).ToArray()),
            _ => ToPublicIngressDto(eligible[0])
        };
    }

    public async Task<IReadOnlyList<DeploymentTargetPublicIngressDto>>
        ListEligiblePublicIngressForNewWorkloadAsync(
            DeploymentTargetAdmissionRequest request,
            CancellationToken cancellationToken = default)
    {
        var targets = await deploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken);

        return targets
            .Select(target => target.ToLookupDto())
            .Where(target => HasRequiredCapabilities(
                target,
                DeploymentTargetRequiredCapabilities.PublicIngress) &&
                !string.IsNullOrWhiteSpace(
                    target.WorkloadConfiguration.Ingress?.PublicBaseDomain))
            .Select(ToPublicIngressDto)
            .ToArray();
    }

    public async Task<Result<
        DeploymentTargetPublicIngressDto,
        DeploymentTargetDomainReservationAdmissionError>> AdmitForDomainReservationAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeploymentTargetId = NormalizeDeploymentTargetId(deploymentTargetId);

        var target = await deploymentTargets
            .Find(DeploymentTargetFilters.ById(normalizedDeploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetNotFound(normalizedDeploymentTargetId));
        }

        var lookup = target.ToLookupDto();
        if (!lookup.Enabled)
        {
            return new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetDisabled(lookup.Id));
        }

        if (lookup.DeletionRequested)
        {
            return new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetDeletionRequested(lookup.Id));
        }

        if (!lookup.AcceptsWorkloads)
        {
            return new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetNewWorkloadsDisabled(lookup.Id));
        }

        if (!HasRequiredCapabilities(
                lookup,
                DeploymentTargetRequiredCapabilities.PublicIngress))
        {
            return new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetPublicIngressUnavailable(lookup.Id));
        }

        return new Ok<DeploymentTargetPublicIngressDto>(
            ToPublicIngressDto(lookup));
    }

    private static bool HasRequiredCapabilities(
        DeploymentTargetLookupDto target,
        DeploymentTargetRequiredCapabilities requiredCapabilities) =>
        !requiredCapabilities.HasFlag(DeploymentTargetRequiredCapabilities.PublicIngress) ||
        target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null;

    private static DeploymentTargetPublicIngressDto ToPublicIngressDto(
        DeploymentTargetLookupDto target)
    {
        var ingress = target.WorkloadConfiguration.Ingress!;
        return new DeploymentTargetPublicIngressDto(
            target.Id,
            target.WorkloadConfiguration.Revision,
            ingress.IngressClassName,
            ingress.ClusterIssuerName,
            ingress.PublicBaseDomain!);
    }

    private async Task<Result<DeploymentTarget, DefaultDeploymentTargetLookupError>>
        LoadDefaultTargetAsync(CancellationToken cancellationToken)
    {
        var defaultTargetId = await settings
            .Find(DeploymentTargetSettingsFilters.Singleton())
            .Project(item => item.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(defaultTargetId))
        {
            return new Err<DefaultDeploymentTargetLookupError>(
                new DefaultDeploymentTargetNotConfigured());
        }

        var normalizedDeploymentTargetId = NormalizeDeploymentTargetId(
            defaultTargetId,
            "DeploymentTargetSettings.DefaultDeploymentTargetId");
        var target = await deploymentTargets
            .Find(DeploymentTargetFilters.ById(normalizedDeploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);

        return target is null
            ? throw new InvalidOperationException(
                $"Configured default deployment target '{normalizedDeploymentTargetId}' does not exist.")
            : new Ok<DeploymentTarget>(target);
    }

    private static string NormalizeDeploymentTargetId(
        string? deploymentTargetId,
        string parameterName = "deploymentTargetId")
    {
        var normalizedDeploymentTargetId = deploymentTargetId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDeploymentTargetId))
        {
            throw new ArgumentException(
                "A deployment target ID is required.",
                parameterName);
        }

        if (!ObjectId.TryParse(normalizedDeploymentTargetId, out _))
        {
            throw new ArgumentException(
                "The deployment target ID must be a MongoDB ObjectId.",
                parameterName);
        }

        return normalizedDeploymentTargetId;
    }
}
