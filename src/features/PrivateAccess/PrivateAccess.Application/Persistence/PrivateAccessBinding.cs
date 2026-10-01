using Pole.Features.PrivateAccess.Domain;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.PrivateAccess.Application.Persistence;

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
        new(session.SubjectUserId, session.ResourceType, session.ResourceId,
            session.ResourceDeploymentId, session.EndpointId, session.GrantRevision);
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
        new(session.DeploymentTargetId, session.RuntimeCellId,
            session.IsolationScopeId, session.KubernetesNamespace,
            session.DeploymentRevision, session.DesiredGeneration,
            session.ServiceName, session.ServicePort, session.TargetPort);

    public static PrivateAccessResolvedBinding From(
        ResourceDeploymentAccessPlacement placement,
        PrivateAccessResolvedEndpoint endpoint) =>
        new(placement.DeploymentTargetId, placement.RuntimeCellId,
            placement.IsolationScopeId, placement.KubernetesNamespace,
            placement.DeploymentRevision, placement.DesiredGeneration,
            endpoint.ServiceName, endpoint.ServicePort, endpoint.TargetPort);
}
