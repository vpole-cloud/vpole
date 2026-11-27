// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed class DeploymentTargetConnectionRequestBodyValidator
    : AbstractValidator<DeploymentTargetConnectionRequestBody>
{
    public DeploymentTargetConnectionRequestBodyValidator()
    {
        RuleFor(body => body.Kind)
            .NotEmpty()
            .Must(kind => Enum.GetNames<DeploymentTargetConnectionKind>()
                .Contains(kind?.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage("Connection kind is invalid.");

        When(
            IsKind(DeploymentTargetConnectionKind.InCluster),
            () =>
            {
                RuleFor(body => body.Kubeconfig).Empty();
                RuleFor(body => body.ContextName).Empty();
                RuleFor(body => body.ServerUrl).Empty();
                RuleFor(body => body.CertificateAuthorityData).Empty();
                RuleFor(body => body.Token).Empty();
            });

        When(
            IsKind(DeploymentTargetConnectionKind.Kubeconfig),
            () =>
            {
                RuleFor(body => body.Kubeconfig).NotEmpty();
                RuleFor(body => body.ServerUrl).Empty();
                RuleFor(body => body.CertificateAuthorityData).Empty();
                RuleFor(body => body.Token).Empty();
            });

        When(
            IsKind(DeploymentTargetConnectionKind.ServiceAccount),
            () =>
            {
                RuleFor(body => body.ServerUrl).NotEmpty();
                RuleFor(body => body.CertificateAuthorityData).NotEmpty();
                RuleFor(body => body.Token).NotEmpty();
                RuleFor(body => body.Kubeconfig).Empty();
                RuleFor(body => body.ContextName).Empty();
            });
    }

    private static Func<DeploymentTargetConnectionRequestBody, bool> IsKind(DeploymentTargetConnectionKind expected) =>
        body => Enum.TryParse<DeploymentTargetConnectionKind>(body.Kind, ignoreCase: true, out var actual) &&
            actual == expected;
}
