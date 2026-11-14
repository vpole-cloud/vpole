using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Common;

internal static class BackupConfigurationNotifications
{
    private const string ArchiveKind = "BackupArchive";

    internal static OutboxWrite DestinationChanged(BackupDestination destination) => Changed(
        destination,
        BackupDeclarationKinds.Destination,
        destination.Revision);

    internal static OutboxWrite DestinationRemoved(BackupDestination destination) => Changed(
        destination,
        BackupDeclarationKinds.Destination,
        destination.Revision + 1);

    internal static OutboxWrite ArchiveForgotten(BackupArchive archive) => Changed(
        archive,
        ArchiveKind,
        archive.ReferenceFence + 1);

    private static OutboxWrite Changed(
        IBackupDocument document,
        string kind,
        long revision) => OutboxWrite.Create(
        new BackupConfigurationChangedEvent(document.Id, kind, revision, EventCore.Create(document.TenantId)));
}
