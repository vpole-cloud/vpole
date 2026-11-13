using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Policies;

internal static class BackupPolicyNotifications
{
    internal static OutboxWrite[] Changed(BackupPolicy policy) =>
    [
        ConfigurationChanged(policy, policy.Revision),
        BackupWorkRequests.Policy(policy),
    ];

    internal static OutboxWrite Removed(BackupPolicy policy) =>
        ConfigurationChanged(policy, checked(policy.Revision + 1));

    private static OutboxWrite ConfigurationChanged(BackupPolicy policy, long revision) => OutboxWrite.Create(
        new BackupConfigurationChangedEvent(
            policy.Id,
            BackupDeclarationKinds.Policy,
            revision,
            EventCore.Create(policy.TenantId) with
            {
                TraceId = policy.Dispatch.Origin.TraceId, CorrelationId = policy.Dispatch.Origin.CorrelationId
            }));
}
