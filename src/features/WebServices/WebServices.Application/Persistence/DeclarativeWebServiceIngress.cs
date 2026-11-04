using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed partial class DeclarativeWebServiceProvider
{
    private async Task<PrepareWebServiceIngressOutcome> PrepareIngressAsync(
        PrepareWebServiceIngressRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var service = request.Service;
        if (service.Exposure.Mode == WebServiceExposureMode.Private)
        {
            foreach (var placement in request.Placements)
            {
                var existing = await loadHostnameBindingsForDeployment(new(service.TenantId,
                    WebServiceResourceDeployment.ResourceType, service.Id, placement.DeploymentId), session, ct);
                if (existing.Any(item => item.Kind == HostnameBindingKinds.Custom))
                    return new WebServiceDeclarationRejected(WebServiceDeclarationError.PublicIngressUnavailable);
            }
            return new PreparedWebServiceIngress(request.Placements.ToDictionary(item => item.DeploymentId,
                item => item.Ingress is null ? (ResourceDeploymentIngressChange)new PreserveIngress() :
                    new ClearIngress(), StringComparer.Ordinal));
        }

        var target = request.Target;
        if (target.WorkloadConfiguration.Ingress is not { PublicBaseDomain: { Length: > 0 } } ingress)
            return new WebServiceDeclarationRejected(WebServiceDeclarationError.PublicIngressUnavailable);

        var zones = await getOrCreateGeneratedDomainZones(
            [new(service.TenantId, target.Id, ingress.PublicBaseDomain, target.WorkloadConfiguration.Revision)],
            session, ct);
        if (zones is GeneratedDomainZoneError)
            return new WebServiceDeclarationRejected(WebServiceDeclarationError.PublicIngressUnavailable);
        var zone = ((IReadOnlyList<TenantDomainZoneSnapshot>)zones.Value).Single();
        var bindings = new Dictionary<string, HostnameBindingSnapshot>(StringComparer.Ordinal);
        foreach (var placement in request.Placements)
        {
            var existing = await loadHostnameBindingsForDeployment(new(service.TenantId,
                WebServiceResourceDeployment.ResourceType, service.Id, placement.DeploymentId), session, ct);
            var generated = existing.SingleOrDefault(item => item.Kind == HostnameBindingKinds.Generated);
            if (generated is null)
                continue;
            if (generated.TenantDomainZoneId != zone.Id)
                return new WebServiceDeclarationRejected(WebServiceDeclarationError.PublicIngressUnavailable);
            bindings.Add(placement.DeploymentId, generated);
        }

        var missing = request.Placements.Where(item => !bindings.ContainsKey(item.DeploymentId)).Select(item =>
            new CreateGeneratedHostnameBindingRequest(service.TenantId, zone.Id, target.Id,
                WebServiceResourceDeployment.ResourceType, service.Id, item.DeploymentId,
                service.Exposure.GeneratedHostnameLabel(service.Id))).ToArray();
        if (missing.Length > 0)
        {
            var created = await createGeneratedHostnameBindings(missing, session, ct);
            if (created is GeneratedHostnameBindingError)
                return new WebServiceDeclarationRejected(WebServiceDeclarationError.PublicIngressUnavailable);
            foreach (var binding in (IReadOnlyList<HostnameBindingSnapshot>)created.Value)
                bindings.Add(binding.ResourceDeploymentId, binding);
        }

        var changes = new Dictionary<string, ResourceDeploymentIngressChange>(StringComparer.Ordinal);
        foreach (var placement in request.Placements)
        {
            var authoritative = await loadHostnameBindingsForDeployment(new(service.TenantId,
                WebServiceResourceDeployment.ResourceType, service.Id, placement.DeploymentId), session, ct);
            changes.Add(placement.DeploymentId, new ReplaceIngress(ResourceDeploymentIngressConfiguration.Create(
                ingress.IngressClassName, ingress.ClusterIssuerName, ingress.PublicBaseDomain,
                authoritative.Select(item => item.Hostname).Distinct().Order(StringComparer.Ordinal).ToArray())));
        }
        return new PreparedWebServiceIngress(changes);
    }
}

internal sealed record PrepareWebServiceIngressRequest(
    WebService Service,
    DeploymentTargetLookupDto Target,
    IReadOnlyList<ResourceDeploymentPlanningItem> Placements);

internal sealed record PreparedWebServiceIngress(IReadOnlyDictionary<string, ResourceDeploymentIngressChange> Changes);

internal union PrepareWebServiceIngressOutcome(
    PreparedWebServiceIngress,
    WebServiceDeclarationRejected);
