using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed class DeploymentTargetLookup(IDeploymentTargetDbContext dbContext)
    : IDeploymentTargetLookup
{
    public async Task<Result<DeploymentTargetLookupDto>> GetAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var idValidation = ValidateId(deploymentTargetId);
        if (idValidation is not null)
            return idValidation;

        var target = await dbContext.DeploymentTargets
            .Find(DeploymentTargetFilters.ById(deploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);

        return target is null
            ? Failures.NotFound("Deployment target not found.", deploymentTargetId)
            : target.ToLookupDto();
    }

    public async Task<Result<DeploymentTargetLookupDto>> GetDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        var defaultTargetId = await dbContext.DeploymentTargetSettings
            .Find(DeploymentTargetSettingsFilters.Singleton())
            .Project(settings => settings.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(defaultTargetId))
            return Failures.Conflict("The default deployment target is not configured.");

        return await GetAsync(defaultTargetId, cancellationToken);
    }

    public async Task<Result<DeploymentTargetLookupDto>> ResolveForNewWorkloadAsync(
        string? deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var lookup = string.IsNullOrWhiteSpace(deploymentTargetId)
            ? await GetDefaultAsync(cancellationToken)
            : await GetAsync(deploymentTargetId, cancellationToken);
        if (!lookup.TryGetValue(out var target, out var failure))
            return failure;

        if (!target.Enabled)
            return Failures.Conflict("The deployment target is disabled.");
        if (target.DeletionRequested)
            return Failures.Conflict("The deployment target is pending deletion.");
        if (!target.AcceptsWorkloads)
            return Failures.Conflict("The deployment target does not accept new workloads.");

        return target;
    }

    public async Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>>
        ListEligibleForNewWorkloadAsync(
            CancellationToken cancellationToken = default)
    {
        var targets = await dbContext.DeploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken);
        return targets
            .Select(target => target.ToLookupDto())
            .ToArray();
    }

    public async Task<Result<DeploymentTargetLookupDto>> ResolveForNewWorkloadAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.ExplicitDeploymentTargetId))
        {
            var resolved = await ResolveForNewWorkloadAsync(
                request.ExplicitDeploymentTargetId,
                cancellationToken);
            if (!resolved.TryGetValue(out var target, out var failure))
                return failure;

            return HasRequiredCapabilities(target, request.RequiredCapabilities)
                ? target
                : RequiredCapabilitiesFailure(request.RequiredCapabilities);
        }

        var eligible = await ListEligibleForNewWorkloadAsync(request, cancellationToken);
        if (!eligible.TryGetValue(out var targets, out var eligibilityFailure))
            return eligibilityFailure;
        if (targets.Count == 0)
            return NoEligibleTargetsFailure(request.RequiredCapabilities);
        if (targets.Count > 1)
        {
            return Failures.SemanticValidation(new Dictionary<string, string[]>
            {
                ["Placement.DeploymentTargetId"] =
                ["Deployment target is required when more than one eligible target is available."]
            });
        }

        return targets[0];
    }

    public async Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>>
        ListEligibleForNewWorkloadAsync(
            DeploymentTargetAdmissionRequest request,
            CancellationToken cancellationToken = default)
    {
        var targets = await dbContext.DeploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken);
        return targets
            .Select(target => target.ToLookupDto())
            .Where(target => HasRequiredCapabilities(target, request.RequiredCapabilities))
            .ToArray();
    }

    public async Task<DeploymentTargetPublicIngressAdmissionResult>
        AdmitForNewWorkloadAsync(
            DeploymentTargetAdmissionRequest request,
            CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.ExplicitDeploymentTargetId))
        {
            var deploymentTargetId = request.ExplicitDeploymentTargetId.Trim();
            if (ValidateId(deploymentTargetId) is not null)
                return new InvalidDeploymentTargetId(deploymentTargetId);

            var target = await dbContext.DeploymentTargets
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

        var targets = await dbContext.DeploymentTargets
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

    public async Task<Result<
        DeploymentTargetPublicIngressDto,
        DeploymentTargetDomainReservationAdmissionError>> AdmitForDomainReservationAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeploymentTargetId = deploymentTargetId?.Trim() ?? string.Empty;
        if (ValidateId(normalizedDeploymentTargetId) is not null)
        {
            return new Err<DeploymentTargetDomainReservationAdmissionError>(
                new InvalidDeploymentTargetId(normalizedDeploymentTargetId));
        }

        var target = await dbContext.DeploymentTargets
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

    private static Failure RequiredCapabilitiesFailure(
        DeploymentTargetRequiredCapabilities requiredCapabilities) =>
        requiredCapabilities.HasFlag(DeploymentTargetRequiredCapabilities.PublicIngress)
            ? Failures.SemanticValidation(new Dictionary<string, string[]>
            {
                ["Placement.DeploymentTargetId"] =
                ["The selected deployment target does not provide public ingress with a public base domain."]
            })
            : Failures.Conflict("The deployment target does not satisfy the requested capabilities.");

    private static Failure NoEligibleTargetsFailure(
        DeploymentTargetRequiredCapabilities requiredCapabilities) =>
        requiredCapabilities.HasFlag(DeploymentTargetRequiredCapabilities.PublicIngress)
            ? Failures.Conflict("No deployment target accepts public workloads.")
            : Failures.Conflict("No deployment target accepts new workloads.");

    private static ValidationFailure? ValidateId(string? deploymentTargetId)
    {
        if (!string.IsNullOrWhiteSpace(deploymentTargetId) &&
            ObjectId.TryParse(deploymentTargetId, out _))
        {
            return null;
        }

        return Failures.ValidationFailure(new Dictionary<string, string[]>
        {
            ["DeploymentTargetId"] = ["Deployment target ID must be a MongoDB ObjectId."]
        });
    }
}
