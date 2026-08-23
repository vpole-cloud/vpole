using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.DeploymentTargets.Application.Common;

public static class DeploymentTargetHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<DeploymentTargetRevisionConflict>(MapRevisionConflict),
            HttpProblemMapping.For<DeploymentTargetDeletionRejected>(MapDeletionRejected),
            HttpProblemMapping.For<DefaultDeploymentTargetDeletionRejected>(MapDefaultDeletion),
            HttpProblemMapping.For<DeploymentTargetHasDurableReferences>(MapReferences),
            HttpProblemMapping.For<DeploymentTargetPublicBaseDomainReferenced>(MapDomainReferences),
            HttpProblemMapping.For<DeploymentTargetPublicBaseDomainConflict>(MapDomainConflict),
            HttpProblemMapping.For<DeploymentTargetIdentityConflict>(MapIdentityConflict),
            HttpProblemMapping.For<DeploymentTargetConnectionRejected>(MapConnectionRejected),
            HttpProblemMapping.For<DeploymentTargetCredentialRewrapConflict>(MapCredentialRewrapConflict)
        ]);

    private static HttpProblem MapRevisionConflict(DeploymentTargetRevisionConflict error) =>
        Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.revision-conflict",
            "Deployment target changed",
            ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapDeletionRejected(DeploymentTargetDeletionRejected error) =>
        Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.deletion-pending",
            "Deployment target is pending deletion",
            ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapDefaultDeletion(DefaultDeploymentTargetDeletionRejected error) =>
        Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.default-delete-forbidden",
            "The default deployment target cannot be deleted",
            ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapReferences(DeploymentTargetHasDurableReferences error) =>
        Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.durable-references-exist",
            "Deployment target still has durable references",
            ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapDomainReferences(
        DeploymentTargetPublicBaseDomainReferenced error) =>
        Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.public-base-domain-referenced",
            "Deployment target public base domain is still referenced",
            ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapDomainConflict(
        DeploymentTargetPublicBaseDomainConflict error) =>
        Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.public-base-domain-conflict",
            "Deployment target public base domain overlaps another target",
            ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapIdentityConflict(DeploymentTargetIdentityConflict _) =>
        Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.identity-conflict",
            "A deployment target with this Kubernetes cluster identity already exists");

    private static HttpProblem MapConnectionRejected(DeploymentTargetConnectionRejected error) =>
        Problem(
            HttpStatusCode.UnprocessableEntity,
            "deployment-targets.connection-rejected",
            "Deployment target connection was rejected",
            ("errors", error.Errors));

    private static HttpProblem MapCredentialRewrapConflict(
        DeploymentTargetCredentialRewrapConflict _) =>
        Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.credential-rewrap-conflict",
            "Deployment-target credentials could not be rewrapped with the active key");

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) =>
        new(
            (int)status,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: extensions.ToDictionary(
                extension => extension.Name,
                extension => extension.Value));
}
