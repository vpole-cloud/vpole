// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record DeploymentTargetConnectionRequestBody(
    string Kind,
    string? Kubeconfig = null,
    string? ContextName = null,
    string? ServerUrl = null,
    string? CertificateAuthorityData = null,
    string? Token = null
)
{
    public override string ToString() => $"DeploymentTargetConnectionRequestBody(Kind={Kind}, Credential=[REDACTED])";
}
