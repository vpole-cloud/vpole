// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Common;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Backups.Application.Destinations;

public sealed record CreateBackupDestinationCommand(
    string TenantId,
    string Id,
    BackupDestinationInput Input
);

public sealed record UpdateBackupDestinationCommand(
    string TenantId,
    string Id,
    long ExpectedRevision,
    BackupDestinationInput Input
);

public enum BackupCredentialGrantPolicy { GrantSelectedKeys, DeclaredSeparately }

public sealed record BackupDestinationMissing;

public sealed record BackupDestinationRevisionConflict;

public sealed record BackupDestinationIdentityImmutable;

public sealed record BackupDestinationGitOwned;

public sealed record BackupDestinationNameAlreadyExists;

public sealed record BackupDestinationScopeUnavailable;

public sealed record BackupDestinationRegistrationConflict;

public sealed record BackupDestinationCredentialGrantDenied;

public union CreateBackupDestinationWriteOutcome(
    BackupDestination,
    BackupDestinationNameAlreadyExists,
    BackupDestinationScopeUnavailable,
    BackupDestinationRegistrationConflict,
    BackupDestinationCredentialGrantDenied);

public union UpdateAcceptedBackupDestinationOutcome(
    BackupDestination,
    BackupDestinationMissing,
    BackupDestinationRevisionConflict,
    BackupDestinationIdentityImmutable);

public union UpdateManualBackupDestinationOutcome(
    BackupDestination,
    BackupDestinationMissing,
    BackupDestinationRevisionConflict,
    BackupDestinationIdentityImmutable,
    BackupDestinationGitOwned);

public sealed class BackupDestinationWrites(
    IMongoCollection<BackupDestination> destinations,
    MongoRunTransaction transactions,
    IGitOpsMutations git,
    AdmitResourceScopeReference admitScope,
    IResourceRegistrar registrar,
    GrantConsumerSecretAccess grant,
    StoreOutboxWrites outbox
)
{
    public Task<CreateBackupDestinationWriteOutcome> CreateAsync(
        CreateBackupDestinationCommand command,
        CancellationToken ct) => transactions.InvokeOutcomeAsync<CreateBackupDestinationWriteOutcome>(
        async (transaction, token) =>
        {
            var result = await CreateAcceptedAsync(
                command,
                BackupCredentialGrantPolicy.GrantSelectedKeys,
                transaction.Session,
                token);
            return result is BackupDestination ? transaction.Commit(result) : transaction.Abort(result);
        },
        ct);

    public Task<UpdateManualBackupDestinationOutcome> UpdateAsync(
        UpdateBackupDestinationCommand command,
        CancellationToken ct) => transactions.InvokeOutcomeAsync<UpdateManualBackupDestinationOutcome>(
        async (transaction, token) =>
        {
            if (!await git.AuthorizeAsync(
                    new(command.TenantId, BackupDeclarationKinds.Destination, command.Id),
                    transaction.Session,
                    token))
                return transaction.Abort(new UpdateManualBackupDestinationOutcome(new BackupDestinationGitOwned()));

            var accepted = await UpdateAcceptedAsync(command, transaction.Session, token);
            UpdateManualBackupDestinationOutcome result = accepted switch
            {
                BackupDestination destination => destination,
                BackupDestinationMissing error => error,
                BackupDestinationRevisionConflict error => error,
                BackupDestinationIdentityImmutable error => error,
            };
            return result is BackupDestination ? transaction.Commit(result) : transaction.Abort(result);
        },
        ct);

    public async Task<CreateBackupDestinationWriteOutcome> CreateAcceptedAsync(
        CreateBackupDestinationCommand command,
        BackupCredentialGrantPolicy credentialGrant,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var input = command.Input;
        if (await admitScope(new(command.TenantId, input.ResourceGroupId, input.IsolationScopeId, null), session, ct)
            is ResourceScopeReferenceUnavailable)
            return new BackupDestinationScopeUnavailable();

        var destination = BackupDestination.Create(
            command.Id,
            command.TenantId,
            input.Name,
            input.ResourceGroupId,
            input.IsolationScopeId,
            input.Storage());
        if (await registrar.RegisterAsync(
                new(
                    command.TenantId,
                    input.ResourceGroupId,
                    new(BackupResourceTypes.Destination, command.Id, input.Name)),
                session,
                ct) is not RegisteredResource)
            return new BackupDestinationRegistrationConflict();

        var grantKeys = credentialGrant switch
        {
            BackupCredentialGrantPolicy.GrantSelectedKeys => true,
            BackupCredentialGrantPolicy.DeclaredSeparately => false,
            _ => throw new ArgumentOutOfRangeException(nameof(credentialGrant)),
        };
        if (grantKeys && await grant(
                new(
                    command.TenantId,
                    input.ResourceGroupId,
                    input.IsolationScopeId,
                    BackupResourceTypes.Destination,
                    command.Id,
                    input.CredentialSecretId,
                    [input.AccessKeyIdKey, input.SecretAccessKeyKey]),
                session,
                ct) is not ConsumerSecretAccessGranted)
            return new BackupDestinationCredentialGrantDenied();

        try
        {
            await destinations.InsertOneAsync(session, destination, cancellationToken: ct);
        }
        catch (MongoWriteException error) when (MongoDuplicateKey.IsForIndex(
                                                    error,
                                                    BackupsIndexes.DestinationName,
                                                    BackupsCollections.Destinations))
        {
            return new BackupDestinationNameAlreadyExists();
        }

        await outbox(session, [BackupConfigurationNotifications.DestinationChanged(destination)], ct);
        return destination;
    }

    public async Task<UpdateAcceptedBackupDestinationOutcome> UpdateAcceptedAsync(
        UpdateBackupDestinationCommand command,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = await destinations
            .Find(session, BackupsFilters<BackupDestination>.ById(command.TenantId, command.Id))
            .SingleOrDefaultAsync(ct);
        if (current is null)
            return new BackupDestinationMissing();
        if (current.Revision != command.ExpectedRevision)
            return new BackupDestinationRevisionConflict();

        var input = command.Input;
        if (current.Storage != input.Storage() || current.ResourceGroupId != input.ResourceGroupId ||
            current.IsolationScopeId != input.IsolationScopeId || current.Name != input.Name)
            return new BackupDestinationIdentityImmutable();

        return current;
    }
}
