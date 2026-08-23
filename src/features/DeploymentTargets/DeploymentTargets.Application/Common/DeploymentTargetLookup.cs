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
        var normalizedDeploymentTargetId = deploymentTargetId?.Trim() ?? string.Empty;
        if (!IsValidId(normalizedDeploymentTargetId))
        {
            return new Err<DeploymentTargetLookupError>(
                new DeploymentTargetLookupError(
                    new InvalidDeploymentTargetId(normalizedDeploymentTargetId)));
        }

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
        var defaultTargetId = await settings
            .Find(DeploymentTargetSettingsFilters.Singleton())
            .Project(item => item.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(defaultTargetId))
        {
            return new Err<DefaultDeploymentTargetLookupError>(
                new DefaultDeploymentTargetLookupError(
                    new DefaultDeploymentTargetNotConfigured()));
        }

        var target = await LookupAsync(defaultTargetId, cancellationToken);
        return target switch
        {
            Ok<DeploymentTargetLookupDto> value => new Ok<DeploymentTargetLookupDto>(value.Value),
            Err<DeploymentTargetLookupError> error =>
                new Err<DefaultDeploymentTargetLookupError>(
                    new DefaultDeploymentTargetLookupError(error.Value)),
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
        var normalizedDeploymentTargetId = deploymentTargetId?.Trim() ?? string.Empty;
        if (!IsValidId(normalizedDeploymentTargetId))
        {
            return new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new InvalidDeploymentTargetId(normalizedDeploymentTargetId)));
        }

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
        var target = await LookupDefaultAsync(cancellationToken);
        if (target is Err<DefaultDeploymentTargetLookupError> lookupError)
        {
            return lookupError.Value switch
            {
                DefaultDeploymentTargetNotConfigured notConfigured =>
                    new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                        new DefaultDeploymentTargetWorkloadAdmissionError(notConfigured)),
                DeploymentTargetLookupError nested => nested switch
                {
                    InvalidDeploymentTargetId invalidId =>
                        new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                            new DefaultDeploymentTargetWorkloadAdmissionError(
                                new DeploymentTargetWorkloadAdmissionError(invalidId))),
                    DeploymentTargetNotFound notFound =>
                        new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                            new DefaultDeploymentTargetWorkloadAdmissionError(
                                new DeploymentTargetWorkloadAdmissionError(notFound))),
                    _ => throw new InvalidOperationException(
                        "Default deployment-target lookup returned an unsupported nested error case.")
                },
                _ => throw new InvalidOperationException(
                    "Default deployment-target lookup returned an unsupported error case.")
            };
        }

        if (target is not Ok<DeploymentTargetLookupDto> targetSuccess)
        {
            throw new InvalidOperationException(
                "Default deployment-target lookup returned an unsupported result case.");
        }

        var admitted = await AdmitForWorkloadAsync(
            targetSuccess.Value.Id,
            cancellationToken);
        return admitted switch
        {
            Ok<DeploymentTargetLookupDto> value => new Ok<DeploymentTargetLookupDto>(value.Value),
            Err<DeploymentTargetWorkloadAdmissionError> error =>
                new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                    new DefaultDeploymentTargetWorkloadAdmissionError(error.Value)),
            _ => throw new InvalidOperationException(
                "Deployment-target admission returned an unsupported result case.")
        };
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
            var deploymentTargetId = request.ExplicitDeploymentTargetId.Trim();
            if (!IsValidId(deploymentTargetId))
                return new InvalidDeploymentTargetId(deploymentTargetId);

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
        var normalizedDeploymentTargetId = deploymentTargetId?.Trim() ?? string.Empty;
        if (!IsValidId(normalizedDeploymentTargetId))
        {
            return new Err<DeploymentTargetDomainReservationAdmissionError>(
                new InvalidDeploymentTargetId(normalizedDeploymentTargetId));
        }

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

    private static bool IsValidId(string? deploymentTargetId) =>
        !string.IsNullOrWhiteSpace(deploymentTargetId) &&
        ObjectId.TryParse(deploymentTargetId, out _);
}
