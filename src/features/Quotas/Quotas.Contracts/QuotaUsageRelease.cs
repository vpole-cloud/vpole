// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Features.Quotas.Contracts;

public sealed record ReleaseQuotaUsageRequest<TSpec>(
    string TenantId,
    TSpec Spec
);

// The caller conditionally transitions its resource and records replay protection in this same transaction.
public interface IQuotaUsageRelease
{
    Task ReleaseAsync<TSpec>(
        ReleaseQuotaUsageRequest<TSpec> request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}
