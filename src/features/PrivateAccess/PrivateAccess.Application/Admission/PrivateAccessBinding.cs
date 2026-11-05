using Pole.Features.PrivateAccess.Application.Sessions;
using Pole.Features.PrivateAccess.Application.Mongo;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Application.Gateway;
using Pole.Features.PrivateAccess.Application.Catalog;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.PrivateAccess.Application.Admission;

internal sealed record PrivateAccessGrantBinding(
    string SubjectUserId,
    string ResourceType,
    string ResourceId,
    string DeploymentId,
    string EndpointId,
    long Revision)
{
    public static PrivateAccessGrantBinding From(PrivateAccessGrant grant) =>
        new(grant.SubjectUserId, grant.ResourceType, grant.ResourceId,
            grant.ResourceDeploymentId, grant.EndpointId, grant.Revision);

    public static PrivateAccessGrantBinding From(PrivateAccessSession session) =>
        new(session.SubjectUserId, session.Grant.ResourceType, session.Grant.ResourceId,
            session.Grant.ResourceDeploymentId, session.Grant.EndpointId, session.Grant.Revision);
}

internal sealed record PrivateAccessResolvedBinding(
    string TargetId,
    string RuntimeCellId,
    string IsolationScopeId,
    string KubernetesNamespace,
    long DeploymentRevision,
    long DesiredGeneration,
    string ServiceName,
    int ServicePort,
    int TargetPort)
{
    public static PrivateAccessResolvedBinding From(PrivateAccessSession session) =>
        new(session.Route.DeploymentTargetId, session.Route.RuntimeCellId,
            session.Route.IsolationScopeId, session.Route.KubernetesNamespace,
            session.Route.DeploymentRevision, session.Route.DesiredGeneration,
            session.Route.ServiceName, session.Route.ServicePort, session.Route.TargetPort);

    public static PrivateAccessResolvedBinding From(
        ResourceDeploymentAccessPlacement placement,
        PrivateAccessResolvedEndpoint endpoint) =>
        new(placement.DeploymentTargetId, placement.RuntimeCellId,
            placement.IsolationScopeId, placement.KubernetesNamespace,
            placement.DeploymentRevision, placement.DesiredGeneration,
            endpoint.ServiceName, endpoint.ServicePort, endpoint.TargetPort);
}
