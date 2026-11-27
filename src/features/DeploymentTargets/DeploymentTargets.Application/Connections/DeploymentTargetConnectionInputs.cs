// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.DeploymentTargets.Application.Connections;

/// <summary>
///     Plaintext connection material accepted only at the deployment-target write boundary.
///     Implementations deliberately redact their string representation.
/// </summary>
public abstract class DeploymentTargetConnectionInput
{
    private protected DeploymentTargetConnectionInput()
    {
    }

    public sealed override string ToString() => $"{GetType().Name}([REDACTED])";
}

public sealed class InClusterConnectionInput : DeploymentTargetConnectionInput
{
}

public sealed class KubeconfigConnectionInput(
    string kubeconfig,
    string? contextName = null
) : DeploymentTargetConnectionInput
{
    public string Kubeconfig { get; } = kubeconfig;
    public string? ContextName { get; } = contextName;
}

public sealed class ServiceAccountConnectionInput(
    string serverUrl,
    string certificateAuthorityData,
    string token
) : DeploymentTargetConnectionInput
{
    public string ServerUrl { get; } = serverUrl;
    public string CertificateAuthorityData { get; } = certificateAuthorityData;
    public string Token { get; } = token;
}
