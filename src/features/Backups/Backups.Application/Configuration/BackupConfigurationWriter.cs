using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Configuration;

public enum BackupCredentialGrantPolicy { GrantSelectedKeys, DeclaredSeparately }

public sealed class BackupConfigurationWriter(
    IMongoCollection<BackupDestination> destinations,
    AdmitResourceScopeReference admitScope,
    IResourceRegistrar registrar,
    GrantConsumerSecretAccess grant,
    StoreOutboxWrites outbox
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
}
