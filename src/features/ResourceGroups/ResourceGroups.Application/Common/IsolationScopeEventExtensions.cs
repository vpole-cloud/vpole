using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.Common;

internal static class IsolationScopeEventExtensions
{
    public static IsolationScopeCreatedEvent ToCreatedEvent(this IsolationScope scope) => new(
        scope.Id,
        scope.ResourceGroupId,
        scope.Name,
        scope.IsDefault)
    {
        EventCore = EventCore.Create(scope.TenantId),
    };

    public static IsolationScopeDeletionRequestedEvent ToDeletionRequestedEvent(this IsolationScope scope)
    {
        if (scope.Lifecycle is not DeletingIsolationScopeLifecycleState)
            throw new InvalidOperationException("The isolation scope is not pending deletion.");

        return new IsolationScopeDeletionRequestedEvent(scope.Id, scope.ResourceGroupId)
        {
            EventCore = EventCore.Create(scope.TenantId),
        };
    }
}
