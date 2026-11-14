using Pole.Features.Backups.Application.Reconciliation;

namespace Pole.Features.Backups.Application.Jobs;

public sealed class BackupPolicyJob(
    ApplyBackupPolicy workflow
)
{
    public Task ExecuteAsync(
        string tenantId,
        string policyId,
        CancellationToken ct = default) => workflow.ExecuteAsync(tenantId, policyId, ct);
}
