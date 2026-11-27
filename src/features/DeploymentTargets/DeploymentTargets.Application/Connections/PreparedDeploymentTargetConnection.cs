// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using k8s;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Connections;

/// <summary>
///     Couples the encrypted value that may be persisted with the client built from the
///     exact same validated plaintext. The caller owns this instance until the candidate
///     client is transferred to a longer-lived owner.
/// </summary>
public sealed class PreparedDeploymentTargetConnection(
    DeploymentTargetConnection connection,
    IKubernetes candidate
) : IDisposable
{
    private IKubernetes? _candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));

    public DeploymentTargetConnection Connection { get; }
        = connection ?? throw new ArgumentNullException(nameof(connection));

    public IKubernetes Candidate =>
        Volatile.Read(ref _candidate) ?? throw new ObjectDisposedException(nameof(PreparedDeploymentTargetConnection));

    public void Dispose() => Interlocked.Exchange(ref _candidate, null)?.Dispose();

    public IKubernetes TakeCandidate() => Interlocked.Exchange(ref _candidate, null) ??
        throw new ObjectDisposedException(nameof(PreparedDeploymentTargetConnection));

    public override string ToString() =>
        $"PreparedDeploymentTargetConnection(Kind={Connection.Kind}, Version={Connection.Version}, Credential=[REDACTED])";
}
