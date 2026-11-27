// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Tenants.Contracts;

namespace Pole.Features.ComputeProfiles.Application.Validation;

public sealed record ComputeProfileTenantAccessValid;

public union ValidateComputeProfileTenantAccessOutcome(
    ComputeProfileTenantAccessValid,
    SelectedComputeProfileTenantsNotFound);

public delegate Task<ValidateComputeProfileTenantAccessOutcome> ValidateComputeProfileTenantAccess(
    ComputeProfileTenantAccess tenantAccess,
    CancellationToken cancellationToken = default);

public sealed class MongoComputeProfileTenantAccessValidator(
    EnsureTenantsExist ensureTenantsExist
)
{
    public async Task<ValidateComputeProfileTenantAccessOutcome> InvokeAsync(
        ComputeProfileTenantAccess tenantAccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantAccess);
        if (tenantAccess is AllTenantsComputeProfileAccess)
            return new ComputeProfileTenantAccessValid();
        if (tenantAccess is not SelectedTenantsComputeProfileAccess selected)
            throw new InvalidOperationException("The compute-profile tenant access kind is not supported.");

        var result = await ensureTenantsExist(selected.TenantIds, cancellationToken);
        return result switch
        {
            TenantsExist => new ComputeProfileTenantAccessValid(),
            TenantSetNotFound error => new SelectedComputeProfileTenantsNotFound(error.TenantIds),
        };
    }
}
