using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints;

public sealed record GetTenantMembersRequest(
    [FromRoute] string TenantId
) : IMediatorRequest<IReadOnlyCollection<TenantMemberDto>>, ITenantMemberReadOperation;

public sealed class GetTenantMembersHandler(ITenantDbContext dbContext)
    : IMediatorHandler<GetTenantMembersRequest, IReadOnlyCollection<TenantMemberDto>>
{
    public async Task<Result<IReadOnlyCollection<TenantMemberDto>>> HandleAsync(
        GetTenantMembersRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Tenants
            .Find(t => t.Id == request.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return tenant is null
            ? Failures.NotFound("Tenant not found.", request.TenantId)
            : tenant.Members.Select(m => m.ToDto()).ToList();
    }
}
