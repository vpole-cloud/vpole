using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Backups.Application.Configuration;

public sealed class ManualBackupConfiguration(
    MongoRunTransaction transactions,
    IGitOpsMutations git,
    BackupConfigurationWriter writer,
    BackupConfigurationDeletions deletions
)
{
    public Task<BackupWriteResult<BackupDestination>> SaveDestinationAsync(
        BackupConfigurationWrite<BackupDestinationInput> write,
        CancellationToken ct) => MutateAsync(
        write.TenantId,
        BackupDeclarationKinds.Destination,
        write.Id,
        (s, t) => writer.SaveDestinationAsync(write, s, t, BackupCredentialGrantPolicy.GrantSelectedKeys),
        ct);

    public Task<BackupWriteResult<BackupPolicy>> SavePolicyAsync(
        BackupConfigurationWrite<BackupPolicyInput> write,
        CancellationToken ct) => MutateAsync(
        write.TenantId,
        BackupDeclarationKinds.Policy,
        write.Id,
        (s, t) => writer.SavePolicyAsync(write, s, t),
        ct);

    public Task<BackupWriteResult<bool>> DeleteAsync(
        string tenantId,
        string kind,
        string id,
        long revision,
        CancellationToken ct) => MutateAsync(
        tenantId,
        kind,
        id,
        (s, t) => kind == BackupDeclarationKinds.Destination
            ? deletions.DeleteDestinationAsync(tenantId, id, revision, s, t)
            : deletions.DeletePolicyAsync(tenantId, id, revision, s, t),
        ct);

    private async Task<BackupWriteResult<T>> MutateAsync<T>(
        string tenantId,
        string kind,
        string id,
        Func<IClientSessionHandle, CancellationToken, Task<BackupWriteResult<T>>> save,
        CancellationToken ct)
    {
        try
        {
            return await transactions.InvokeOutcomeAsync<BackupWriteResult<T>>(
                async (tx, token) =>
                {
                    if (!await git.AuthorizeAsync(new(tenantId, kind, id), tx.Session, token))
                        return tx.Abort(new BackupWriteResult<T>.Rejected("git-owned"));
                    var result = await save(tx.Session, token);
                    return result is BackupWriteResult<T>.Saved ? tx.Commit(result) : tx.Abort(result);
                },
                ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new BackupWriteResult<T>.Rejected("identity-conflict");
        }
    }
}
