using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Destinations;

namespace Pole.Features.Backups.Application.Common;

public static class BackupDestinationHttpMappings
{
    public static HttpProblem MapDestinationMissing(BackupDestinationMissing _) => Problem(
        404,
        "backups.not-found",
        "Backup destination not found");

    public static HttpProblem MapDestinationNameExists(BackupDestinationNameAlreadyExists _) => Problem(
        409,
        "backups.destination-name-exists",
        "A backup destination with this name exists");

    public static HttpProblem MapDestinationScopeUnavailable(BackupDestinationScopeUnavailable _) => Problem(
        422,
        "backups.scope-unavailable",
        "Backup destination scope is unavailable");

    public static HttpProblem MapDestinationRegistrationConflict(BackupDestinationRegistrationConflict _) => Problem(
        409,
        "backups.registration-conflict",
        "Backup destination registration changed");

    public static HttpProblem MapDestinationCredentialGrantDenied(BackupDestinationCredentialGrantDenied _) => Problem(
        422,
        "backups.credential-grant-denied",
        "Access to the selected credential keys is unavailable");

    public static HttpProblem MapDestinationRevisionConflict(BackupDestinationRevisionConflict _) => Problem(
        409,
        "backups.revision-conflict",
        "Backup destination changed");

    public static HttpProblem MapDestinationIdentityImmutable(BackupDestinationIdentityImmutable _) => Problem(
        409,
        "backups.destination-identity-immutable",
        "Backup destination identity cannot change");

    public static HttpProblem MapDestinationGitOwned(BackupDestinationGitOwned _) => Problem(
        409,
        "backups.git-owned",
        "Backup destination is managed by Git");

    private static HttpProblem Problem(
        int status,
        string code,
        string title) => new(status, PoleProblemTypeId.FromCode(code), title, code);
}
