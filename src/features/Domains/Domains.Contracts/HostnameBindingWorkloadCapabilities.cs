// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.Domains.Contracts;

public enum HostnameAttachmentAvailability
{
    Active,
    AwaitingArtifact,
}

public sealed record ReadHostnameAttachmentRequest(
    string TenantId,
    string ResourceId,
    string DeploymentTargetId,
    string Hostname
);

public sealed record HostnameAttachmentDocumentVersion(
    string DocumentId,
    long Version
);

public sealed record HostnameAttachmentSnapshot(
    string TenantId,
    string ResourceId,
    ResourceTypeId ResourceType,
    string DeploymentTargetId,
    string DeploymentId,
    string Hostname,
    IReadOnlyList<string> CurrentHostnames,
    IReadOnlyList<HostnameAttachmentDocumentVersion> DocumentVersions,
    HostnameAttachmentAvailability Availability
);

public sealed record HostnameAttachmentUnavailable;

public sealed record HostnameAttachmentPlacementUnavailable;

public union ReadHostnameAttachmentOutcome(
    HostnameAttachmentSnapshot,
    HostnameAttachmentUnavailable,
    HostnameAttachmentPlacementUnavailable);

public sealed record HostnameAttachmentFenced;

public sealed record HostnameAttachmentPreconditionsChanged;

public union FenceHostnameAttachmentOutcome(
    HostnameAttachmentFenced,
    HostnameAttachmentPreconditionsChanged,
    HostnameAttachmentUnavailable,
    HostnameAttachmentPlacementUnavailable);

public sealed record HostnameAttachmentProvenance(
    string SourceId,
    string RunId,
    string Commit
);

public sealed record ApplyHostnameAttachmentRequest(
    HostnameAttachmentSnapshot Snapshot,
    string BindingId,
    HostnameAttachmentProvenance Provenance
);

public sealed record HostnameAttachmentApplied(
    bool Changed
);

public sealed record HostnameAttachmentBindingRejected;

public union ApplyHostnameAttachmentOutcome(
    HostnameAttachmentApplied,
    HostnameAttachmentPreconditionsChanged,
    HostnameAttachmentUnavailable,
    HostnameAttachmentPlacementUnavailable,
    HostnameAttachmentBindingRejected);

/// <summary>
///     Workload owners implement the narrow hostname-attachment mutation. An attachment does not
///     transfer resource ownership or authorize changes to the workload's other configuration.
/// </summary>
public interface IHostnameBindingWorkloadCapability
{
    string Kind { get; }

    ResourceTypeId ResourceType { get; }

    Task<ReadHostnameAttachmentOutcome> ReadAsync(
        ReadHostnameAttachmentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);

    Task<FenceHostnameAttachmentOutcome> FenceAsync(
        HostnameAttachmentSnapshot snapshot,
        IClientSessionHandle session,
        CancellationToken cancellationToken);

    Task<ApplyHostnameAttachmentOutcome> ApplyAsync(
        ApplyHostnameAttachmentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}

public sealed record RemoveHostnameAttachmentRequest(
    HostnameAttachmentSnapshot Snapshot,
    string BindingId,
    HostnameAttachmentProvenance Provenance
);

public sealed record HostnameAttachmentOwnerGone;

public sealed record HostnameAttachmentOwnerDeleting;

public union ReadHostnameAttachmentRemovalOutcome(
    HostnameAttachmentSnapshot,
    HostnameAttachmentOwnerGone,
    HostnameAttachmentOwnerDeleting,
    HostnameAttachmentUnavailable,
    HostnameAttachmentPlacementUnavailable);

public interface IHostnameBindingRemovalCapability : IHostnameBindingWorkloadCapability
{
    Task<ReadHostnameAttachmentRemovalOutcome> ReadRemovalAsync(
        ReadHostnameAttachmentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);

    Task<ApplyHostnameAttachmentOutcome> RemoveAsync(
        RemoveHostnameAttachmentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}
