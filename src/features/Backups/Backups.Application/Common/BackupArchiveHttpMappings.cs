using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Contracts;

namespace Pole.Features.Backups.Application.Common;

public sealed record BackupAdministrationRequired;

public static class BackupArchiveHttpMappings
{
    public static HttpProblem MapArchiveMissing(BackupArchiveMissing _) => Problem(
        404,
        "backups.not-found",
        "Backup archive not found");

    public static HttpProblem MapArchiveInUse(BackupArchiveInUse _) => Problem(
        409,
        "backups.archive-in-use",
        "Backup archive is still in use");

    public static HttpProblem MapArchivePlacementConflict(ArchivePlacementConflict _) => Problem(
        409,
        "backups.placement-conflict",
        "Backup credential placement changed");

    public static HttpProblem MapArchiveObservationUnavailable(ArchiveObservationUnavailable _) => Problem(
        503,
        "backups.archive-observation-unavailable",
        "Native backup state could not be checked");

    public static HttpProblem MapAdministrationRequired(BackupAdministrationRequired _) => Problem(
        403,
        "backups.admin-required",
        "Tenant administration is required");

    public static HttpProblem MapCatalogCredentialUnavailable(BackupCatalogCredentialUnavailable _) => Problem(
        503,
        "backups.catalog-credential-unavailable",
        "Catalog credential access is unavailable");

    public static HttpProblem MapCatalogAccessDenied(BackupCatalogAccessDenied _) => Problem(
        503,
        "backups.backup-catalog-access-denied",
        "Object storage rejected backup credentials");

    public static HttpProblem MapCatalogUnavailable(BackupCatalogUnavailable _) => Problem(
        503,
        "backups.backup-catalog-unavailable",
        "Backup catalog is unavailable");

    public static HttpProblem MapCatalogUnreadable(BackupCatalogUnreadable _) => Problem(
        503,
        "backups.backup-catalog-unreadable",
        "Backup catalog metadata is unreadable");

    public static HttpProblem MapCatalogTimeout(BackupCatalogTimeout _) => Problem(
        503,
        "backups.backup-catalog-timeout",
        "Backup catalog request timed out");

    private static HttpProblem Problem(
        int status,
        string code,
        string title) => new(status, PoleProblemTypeId.FromCode(code), title, code);
}
