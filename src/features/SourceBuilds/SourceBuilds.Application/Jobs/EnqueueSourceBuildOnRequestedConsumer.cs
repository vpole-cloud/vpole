// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public sealed class EnqueueSourceBuildOnRequestedConsumer(
    DispatchSourceBuildJob dispatchSourceBuildJob
) : IConsumer<SourceBuildRequestedEvent>
{
    public Task Consume(ConsumeContext<SourceBuildRequestedEvent> context)
    {
        dispatchSourceBuildJob(context.Message.BuildJobId);
        return Task.CompletedTask;
    }
}

public sealed class EnqueueSourceBuildOnRequestedConsumerDefinition
    : ConsumerDefinition<EnqueueSourceBuildOnRequestedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<EnqueueSourceBuildOnRequestedConsumer> consumerConfigurator,
        IRegistrationContext context) => endpointConfigurator.UseMessageRetry(retry =>
        retry.Intervals(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));
}
