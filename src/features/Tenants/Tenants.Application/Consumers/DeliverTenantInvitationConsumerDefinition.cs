// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;

namespace Pole.Features.Tenants.Application.Consumers;

public sealed class DeliverTenantInvitationConsumerDefinition : ConsumerDefinition<DeliverTenantInvitationConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DeliverTenantInvitationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry =>
        {
            retry.Ignore<OperationCanceledException>();
            retry.Ignore<InvalidOperationException>();
            retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        });
    }
}
