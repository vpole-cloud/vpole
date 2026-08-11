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
            .Find(item => item.Id == deploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);

        return target is null
            ? Failures.NotFound("Deployment target not found.", deploymentTargetId)
            : target.ToLookupDto();
    }

    public async Task<Result<DeploymentTargetLookupDto>> GetDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        var defaultTargetId = await dbContext.DeploymentTargetSettings
            .Find(settings => settings.Id == DeploymentTargetSettings.SingletonId)
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

        // Health is observational and can recover. Admission is controlled only by the durable
        // Enabled and AcceptsWorkloads flags; a stale degraded observation must not become a latch.
        return target;
    }

    public async Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>>
        ListEligibleForNewWorkloadAsync(
            CancellationToken cancellationToken = default)
    {
        var targets = await dbContext.DeploymentTargets
            .Find(target =>
                target.Enabled &&
                target.AcceptsWorkloads &&
                target.DeletionRequestedAtUtc == null)
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
        var requestFailure = ValidateAdmissionRequest(request);
        if (requestFailure is not null)
            return requestFailure;

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
        var requestFailure = ValidateAdmissionRequest(request);
        if (requestFailure is not null)
            return requestFailure;

        // Target ownership is platform-shared in #39. Keeping this request-shaped filter is the
        // admission boundary for the future PlatformShared || TenantOwned || Allowlisted policy.
        var targets = await dbContext.DeploymentTargets
            .Find(target =>
                target.Enabled &&
                target.AcceptsWorkloads &&
                target.DeletionRequestedAtUtc == null)
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken);
        return targets
            .Select(target => target.ToLookupDto())
            .Where(target => HasRequiredCapabilities(target, request.RequiredCapabilities))
            .ToArray();
    }

    public async Task<Result<DeploymentTargetPublicIngressDto>>
        ResolvePublicIngressForNewWorkloadAsync(
            DeploymentTargetAdmissionRequest request,
            CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveForNewWorkloadAsync(
            request with
            {
                RequiredCapabilities = request.RequiredCapabilities |
                    DeploymentTargetRequiredCapabilities.PublicIngress
            },
            cancellationToken);
        if (!resolved.TryGetValue(out var target, out var failure))
            return failure;

        var ingress = target.WorkloadConfiguration.Ingress!;
        return new DeploymentTargetPublicIngressDto(
            target.Id,
            target.WorkloadConfiguration.Revision,
            ingress.IngressClassName,
            ingress.ClusterIssuerName,
            ingress.PublicBaseDomain!);
    }

    public async Task<Result<DeploymentTargetPublicIngressDto>>
        ResolvePublicIngressForDomainReservationAsync(
            string tenantId,
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
    {
        if (!ObjectId.TryParse(tenantId, out _))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["TenantId"] = ["Tenant ID must be a MongoDB ObjectId."]
            });
        }

        var resolved = await ResolveForNewWorkloadAsync(
            deploymentTargetId,
            cancellationToken);
        if (!resolved.TryGetValue(out var target, out var failure))
            return failure;
        if (!HasRequiredCapabilities(
                target,
                DeploymentTargetRequiredCapabilities.PublicIngress))
        {
            return RequiredCapabilitiesFailure(
                DeploymentTargetRequiredCapabilities.PublicIngress);
        }

        var ingress = target.WorkloadConfiguration.Ingress!;
        return new DeploymentTargetPublicIngressDto(
            target.Id,
            target.WorkloadConfiguration.Revision,
            ingress.IngressClassName,
            ingress.ClusterIssuerName,
            ingress.PublicBaseDomain!);
    }

    private static bool HasRequiredCapabilities(
        DeploymentTargetLookupDto target,
        DeploymentTargetRequiredCapabilities requiredCapabilities) =>
        !requiredCapabilities.HasFlag(DeploymentTargetRequiredCapabilities.PublicIngress) ||
        target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null;

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

    private static ValidationFailure? ValidateAdmissionRequest(
        DeploymentTargetAdmissionRequest? request)
    {
        if (request is null || !ObjectId.TryParse(request.TenantId, out _))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["TenantId"] = ["Tenant ID must be a MongoDB ObjectId."]
            });
        }
        if (request.IsolationScopeId is not null &&
            !ObjectId.TryParse(request.IsolationScopeId, out _))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["IsolationScopeId"] = ["Isolation scope ID must be a MongoDB ObjectId when specified."]
            });
        }
        if (request.ExplicitDeploymentTargetId is not null &&
            !ObjectId.TryParse(request.ExplicitDeploymentTargetId, out _))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["DeploymentTargetId"] = ["Deployment target ID must be a MongoDB ObjectId when specified."]
            });
        }
        if ((request.RequiredCapabilities & ~DeploymentTargetRequiredCapabilities.PublicIngress) != 0)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["RequiredCapabilities"] = ["The requested deployment target capability is not supported."]
            });
        }

        return null;
    }

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
