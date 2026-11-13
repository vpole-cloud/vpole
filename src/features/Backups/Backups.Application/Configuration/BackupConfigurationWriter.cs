using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Scheduling;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Configuration;

public enum BackupCredentialGrantPolicy { GrantSelectedKeys, DeclaredSeparately }

public sealed class BackupConfigurationWriter(
    IMongoCollection<BackupDestination> destinations,
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupArchive> archives,
    BackupProviders providers,
    BackupArchiveFactory archiveFactory,
    AdmitResourceScopeReference admitScope,
    IResourceRegistrar registrar,
    GrantConsumerSecretAccess grant,
    BackupOperations operations,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    public async Task<BackupWriteResult<BackupDestination>> SaveDestinationAsync(
        BackupConfigurationWrite<BackupDestinationInput> write,
        IClientSessionHandle session,
        CancellationToken ct,
        BackupCredentialGrantPolicy credentialGrant)
    {
        var input = write.Input;
        if (!BackupInputRules.Valid(input))
            return new BackupWriteResult<BackupDestination>.Rejected("invalid-destination");
        var id = BackupsFilters<BackupDestination>.ById(write.TenantId, write.Id);
        var current = await destinations.Find(session, id).SingleOrDefaultAsync(ct);
        if ((current?.Revision ?? 0) != write.ExpectedRevision)
            return new BackupWriteResult<BackupDestination>.Rejected("revision-conflict");
        var storage = input.Storage();
        if (current is not null && (current.Storage != storage || current.ResourceGroupId != input.ResourceGroupId ||
                current.IsolationScopeId != input.IsolationScopeId || current.Name != input.Name))
            return new BackupWriteResult<BackupDestination>.Rejected("destination-identity-immutable");
        if (current is not null)
            return new BackupWriteResult<BackupDestination>.Saved(current);
        if (await admitScope(new(write.TenantId, input.ResourceGroupId, input.IsolationScopeId, null), session, ct)
            is ResourceScopeReferenceUnavailable)
            return new BackupWriteResult<BackupDestination>.Rejected("scope-unavailable");
        var destination = BackupDestination.Create(
            write.Id,
            write.TenantId,
            input.Name,
            input.ResourceGroupId,
            input.IsolationScopeId,
            storage);
        if (await registrar.RegisterAsync(
                new(write.TenantId, input.ResourceGroupId, new(BackupResourceTypes.Destination, write.Id, input.Name)),
                session,
                ct) is not RegisteredResource)
            return new BackupWriteResult<BackupDestination>.Rejected("registration-conflict");
        if (credentialGrant == BackupCredentialGrantPolicy.GrantSelectedKeys && await grant(
                new(
                    write.TenantId,
                    input.ResourceGroupId,
                    input.IsolationScopeId,
                    BackupResourceTypes.Destination,
                    write.Id,
                    storage.CredentialSecretId,
                    [storage.AccessKeyIdKey, storage.SecretAccessKeyKey]),
                session,
                ct) is not ConsumerSecretAccessGranted)
            return new BackupWriteResult<BackupDestination>.Rejected("credential-grant-denied");
        await destinations.InsertOneAsync(session, destination, cancellationToken: ct);
        await Changed(session, destination, BackupDeclarationKinds.Destination, destination.Revision, ct);
        return new BackupWriteResult<BackupDestination>.Saved(destination);
    }

    public async Task<BackupWriteResult<BackupPolicy>> SavePolicyAsync(
        BackupConfigurationWrite<BackupPolicyInput> write,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var input = write.Input;
        if (!BackupInputRules.Valid(input))
            return new BackupWriteResult<BackupPolicy>.Rejected("invalid-policy");
        var provider = providers.Find(input.ResourceType);
        if (provider is null)
            return new BackupWriteResult<BackupPolicy>.Rejected("unsupported-provider");
        var schedule = new BackupSchedule(
            UtcCronSchedule.Parse(input.Schedule).Expression,
            input.Suspended,
            input.RetentionDays);
        if (provider.ValidateSchedule(schedule) is BackupRetentionInvalid)
            return new BackupWriteResult<BackupPolicy>.Rejected("backup-positive-retention-required");
        var filter = BackupsFilters<BackupPolicy>.ById(write.TenantId, write.Id);
        var current = await policies.Find(session, filter).SingleOrDefaultAsync(ct);
        if ((current?.Revision ?? 0) != write.ExpectedRevision)
            return new BackupWriteResult<BackupPolicy>.Rejected("revision-conflict");
        if (current is { Deleting: true } || current is not null && (current.Name != input.Name ||
                current.ResourceType != input.ResourceType || current.ResourceId != input.ResourceId))
            return new BackupWriteResult<BackupPolicy>.Rejected("policy-identity-immutable");
        if (current is not null && current.DestinationId == input.DestinationId &&
            current.Schedule == schedule.CronUtc && current.Suspended == schedule.Suspended &&
            current.RetentionDays == schedule.RetentionDays)
            return new BackupWriteResult<BackupPolicy>.Saved(current);
        var destination = await destinations.FindOneAndUpdateAsync(
            session,
            BackupsFilters<BackupDestination>.ById(write.TenantId, input.DestinationId),
            Builders<BackupDestination>.Update.Inc(d => d.ReferenceFence, 1),
            cancellationToken: ct);
        if (destination is null)
            return new BackupWriteResult<BackupPolicy>.Rejected("destination-unavailable");

        var archiveId = current?.ArchiveId;
        if (current?.DestinationId != destination.Id)
        {
            var created = await archiveFactory.CreateAsync(write, destination, provider, session, ct);
            if (created is BackupWriteResult<BackupArchive>.Rejected failure)
                return new BackupWriteResult<BackupPolicy>.Rejected(failure.Code);
            var archive = ((BackupWriteResult<BackupArchive>.Saved)created).Value;
            archiveId = archive.Id;
            if (current is not null)
                await RetireArchive(current, session, ct, sourceRemoved: false, replacement: archive);
        }

        var eventCore = EventCore.Create(write.TenantId);
        var origin = new BackupExecutionOrigin(eventCore.TraceId, eventCore.CorrelationId);
        var now = clock.GetUtcNow().UtcDateTime;
        var policy = current is null
            ? BackupPolicyFactory.Create(
                write.Id,
                write.TenantId,
                input.Name,
                input.ResourceType,
                input.ResourceId,
                destination.Id,
                archiveId!,
                schedule.CronUtc,
                schedule.Suspended,
                schedule.RetentionDays,
                origin,
                now)
            : current.Reconfigure(
                destination.Id,
                archiveId!,
                schedule.CronUtc,
                schedule.Suspended,
                schedule.RetentionDays,
                origin,
                now);
        if (current is null)
            await policies.InsertOneAsync(session, policy, cancellationToken: ct);
        else
            await policies.ReplaceOneAsync(session, filter, policy, cancellationToken: ct);
        await PolicyChanged(session, policy, ct);
        return new BackupWriteResult<BackupPolicy>.Saved(policy);
    }

    internal async Task RetireArchive(
        BackupPolicy policy,
        IClientSessionHandle session,
        CancellationToken ct,
        bool sourceRemoved,
        BackupArchive? replacement = null)
    {
        var update = Builders<BackupArchive>.Update.Set(a => a.Active, false);
        if (replacement is not null)
            update = update.Set(a => a.ServiceVersion, replacement.ServiceVersion)
                .Max(a => a.SourceRevision, replacement.SourceRevision)
                .Max(a => a.StorageGiB, replacement.StorageGiB)
                .Set(a => a.Instances, replacement.Instances);
        await archives.UpdateOneAsync(
            session,
            BackupsFilters<BackupArchive>.ById(policy.TenantId, policy.ArchiveId),
            update,
            cancellationToken: ct);
        await operations.InterruptScheduledObservationsAsync(
            policy.TenantId,
            policy.ArchiveId,
            sourceRemoved,
            session,
            ct);
    }

    internal async Task PolicyChanged(
        IClientSessionHandle session,
        BackupPolicy policy,
        CancellationToken ct)
    {
        await Changed(session, policy, BackupDeclarationKinds.Policy, policy.Revision, ct);
        await outbox(session, [BackupWorkRequests.Policy(policy)], ct);
    }

    internal Task Changed(
        IClientSessionHandle session,
        IBackupDocument entity,
        string kind,
        long revision,
        CancellationToken ct) => outbox(
        session,
        [
            OutboxWrite.Create(
                new BackupConfigurationChangedEvent(entity.Id, kind, revision, EventCore.Create(entity.TenantId)))
        ],
        ct);
}

public static partial class BackupInputRules
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,61}[a-z0-9]$|^[a-z]$")]
    private static partial Regex Name();

    public static bool ValidName(string value) => !string.IsNullOrWhiteSpace(value) && Name().IsMatch(value);

    public static bool Valid(BackupDestinationInput d) => ValidName(d.Name) &&
        ObjectId.TryParse(d.ResourceGroupId, out _) && ObjectId.TryParse(d.IsolationScopeId, out _) &&
        ValidStorageLocation(d.Endpoint, d.Bucket, d.Prefix) && ObjectId.TryParse(d.CredentialSecretId, out _) &&
        ValidKeys(d.AccessKeyIdKey, d.SecretAccessKeyKey);

    public static bool ValidEndpoint(string? endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 && uri.AbsolutePath == "/";

    public static bool ValidBucket(string? bucket) => bucket is { Length: >= 3 and <= 63 } &&
        Regex.IsMatch(bucket, "^[a-z0-9][a-z0-9.-]*[a-z0-9]$");

    public static bool ValidPrefix(string? prefix) => prefix is { Length: <= 512 } && !prefix.Any(char.IsControl) &&
        prefix.Split('/').All(segment => segment != ".." && segment != ".");

    public static bool ValidStorageLocation(
        string endpoint,
        string bucket,
        string prefix) => ValidEndpoint(endpoint) && ValidBucket(bucket) && ValidPrefix(prefix);

    public static bool ValidKeys(string accessKeyIdKey, string secretAccessKeyKey) =>
        SecretKeyReferenceRules.ValidKey(accessKeyIdKey) && SecretKeyReferenceRules.ValidKey(secretAccessKeyKey) &&
        accessKeyIdKey != secretAccessKeyKey;

    public static bool Valid(BackupPolicyInput p) => ValidName(p.Name) &&
        ResourceTypeId.TryCreate(p.ResourceType, out _) && ObjectId.TryParse(p.ResourceId, out _) &&
        ObjectId.TryParse(p.DestinationId, out _) && p.Schedule is { Length: <= 120 } &&
        UtcCronSchedule.TryParse(p.Schedule, out _);
}
