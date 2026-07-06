using MassTransit;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Contracts;

namespace Pole.Features.Tenants.Application.Consumers;

public sealed class CreateDefaultTenantOnUserCreatedConsumer(ITenantDbContext dbContext)
    : IConsumer<UserCreatedEvent>
{
    public Task Consume(ConsumeContext<UserCreatedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public async Task Handle(UserCreatedEvent message, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.Tenants
            .Find(tenant => tenant.Id == message.DefaultTenantId)
            .AnyAsync(cancellationToken);
        if (exists)
            return;

        var tenant = TenantFactory.Create(message.DefaultTenantName, message.DefaultTenantId);
        var owner = TenantFactory.CreateMember(
            tenant.Id,
            message.UserId,
            message.Email,
            TenantRole.Owner,
            isDefault: true);

        tenant = tenant with { Members = [owner] };

        await dbContext.Tenants.InsertOneAsync(tenant, cancellationToken: cancellationToken);
    }
}
