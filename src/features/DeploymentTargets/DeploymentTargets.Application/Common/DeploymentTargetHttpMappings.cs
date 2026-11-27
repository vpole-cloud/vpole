// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Contracts.HttpMapping;

namespace Pole.Features.DeploymentTargets.Application.Common;

public static class DeploymentTargetHttpMappings
{
    public static IResult Map(DeploymentTargetNotFound error) =>
        DeploymentTargetContractHttpMappings.MapNotFound(error).ToHttpResult();

    public static IResult Map(DeploymentTargetRevisionConflict error) => MapRevisionConflict(error).ToHttpResult();

    public static IResult Map(DeploymentTargetDeletionRejected error) => MapDeletionRejected(error).ToHttpResult();

    public static IResult Map(DefaultDeploymentTargetDeletionRejected error) =>
        MapDefaultDeletion(error).ToHttpResult();

    public static IResult Map(DeploymentTargetHasDurableReferences error) => MapReferences(error).ToHttpResult();

    public static IResult Map(DeploymentTargetPublicBaseDomainReferenced error) =>
        MapDomainReferences(error).ToHttpResult();

    public static IResult Map(DeploymentTargetPublicBaseDomainConflict error) =>
        MapDomainConflict(error).ToHttpResult();

    public static IResult Map(DeploymentTargetTechnicalNameConflict error) => Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.technical-name-conflict",
            "The catalog technical name is already used")
        .ToHttpResult();

    public static IResult Map(DeploymentTargetIdentityConflict error) => MapIdentityConflict(error).ToHttpResult();

    public static IResult Map(DeploymentTargetConnectionRejected error) => MapConnectionRejected(error).ToHttpResult();

    public static IResult Map(DeploymentTargetCredentialRewrapConflict error) =>
        MapCredentialRewrapConflict(error).ToHttpResult();

    public static IResult Map(DeleteDeploymentTargetError error) => error switch
    {
        DeploymentTargetNotFound value => Map(value),
        DeploymentTargetRevisionConflict value => Map(value),
        DefaultDeploymentTargetDeletionRejected value => Map(value),
        DeploymentTargetHasDurableReferences value => Map(value),
    };

    private static HttpProblem MapRevisionConflict(DeploymentTargetRevisionConflict error) => Problem(
        HttpStatusCode.Conflict,
        "deployment-targets.revision-conflict",
        "Deployment target changed",
        ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapDeletionRejected(DeploymentTargetDeletionRejected error) => Problem(
        HttpStatusCode.Conflict,
        "deployment-targets.deletion-pending",
        "Deployment target is pending deletion",
        ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapDefaultDeletion(DefaultDeploymentTargetDeletionRejected error) => Problem(
        HttpStatusCode.Conflict,
        "deployment-targets.default-delete-forbidden",
        "The default deployment target cannot be deleted",
        ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapReferences(DeploymentTargetHasDurableReferences error) => Problem(
        HttpStatusCode.Conflict,
        "deployment-targets.durable-references-exist",
        "Deployment target still has durable references",
        ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapDomainReferences(DeploymentTargetPublicBaseDomainReferenced error) => Problem(
        HttpStatusCode.Conflict,
        "deployment-targets.public-base-domain-referenced",
        "Deployment target public base domain is still referenced",
        ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapDomainConflict(DeploymentTargetPublicBaseDomainConflict error) => Problem(
        HttpStatusCode.Conflict,
        "deployment-targets.public-base-domain-conflict",
        "Deployment target public base domain overlaps another target",
        ("deploymentTargetId", error.DeploymentTargetId));

    private static HttpProblem MapIdentityConflict(DeploymentTargetIdentityConflict _) => Problem(
        HttpStatusCode.Conflict,
        "deployment-targets.identity-conflict",
        "A deployment target with this Kubernetes cluster identity already exists");

    private static HttpProblem MapConnectionRejected(DeploymentTargetConnectionRejected error) => Problem(
        HttpStatusCode.UnprocessableEntity,
        "deployment-targets.connection-rejected",
        "Deployment target connection was rejected",
        ("errors", error.Errors));

    private static HttpProblem MapCredentialRewrapConflict(DeploymentTargetCredentialRewrapConflict _) => Problem(
        HttpStatusCode.Conflict,
        "deployment-targets.credential-rewrap-conflict",
        "Deployment-target credentials could not be rewrapped with the active key");

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) => new(
        (int)status,
        PoleProblemTypeId.FromCode(code),
        title,
        code,
        extensions: extensions.ToDictionary(extension => extension.Name, extension => extension.Value));
}
