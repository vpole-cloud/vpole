using MassTransit;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Events;

namespace Pole.Features.Tenants.Application.Consumers;

public sealed class CreateDefaultTenantOnUserCreatedConsumer(
    EnsureDefaultTenant ensureDefaultTenant)
    : IConsumer<UserCreatedEvent>
{
    public Task Consume(ConsumeContext<UserCreatedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public async Task Handle(UserCreatedEvent message, CancellationToken cancellationToken = default)
    {
        var tenant = TenantFactory.Create(message.DefaultTenantName, message.DefaultTenantId);
        var owner = TenantFactory.CreateMember(
            tenant.Id,
            message.UserId,
            message.Email,
            TenantRole.Owner,
            isDefault: true);

        tenant = tenant with { Members = [owner] };

        await ensureDefaultTenant(tenant, cancellationToken);
    }
}
