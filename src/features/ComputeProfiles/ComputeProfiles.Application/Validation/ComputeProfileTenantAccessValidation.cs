using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Tenants.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Validation;

public delegate Task<Result<Unit, SelectedComputeProfileTenantsNotFound>>
    ValidateComputeProfileTenantAccess(
    ComputeProfileTenantAccess tenantAccess,
    CancellationToken cancellationToken = default);

public sealed class MongoComputeProfileTenantAccessValidator(
    EnsureTenantsExist ensureTenantsExist)
{
    public async Task<Result<Unit, SelectedComputeProfileTenantsNotFound>> InvokeAsync(
        ComputeProfileTenantAccess tenantAccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantAccess);
        if (tenantAccess is AllTenantsComputeProfileAccess)
            return new Ok<Unit>(Unit.Value);
        if (tenantAccess is not SelectedTenantsComputeProfileAccess selected)
            throw new InvalidOperationException("The compute-profile tenant access kind is not supported.");

        var result = await ensureTenantsExist(selected.TenantIds, cancellationToken);
        return result switch
        {
            Ok<Unit> => new Ok<Unit>(Unit.Value),
            Err<TenantSetNotFound> error =>
                new Err<SelectedComputeProfileTenantsNotFound>(
                    new SelectedComputeProfileTenantsNotFound(error.Value.TenantIds))
        };
    }
}
