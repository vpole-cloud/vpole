// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Contracts;

namespace Pole.Features.Backups.Application.Common;

public static class BackupPolicyHttpMappings
{
    public static HttpProblem MapSourceUnavailable(BackupSourceUnavailable _) => Problem(
        422,
        "backups.source-unavailable",
        "Backup source is unavailable");

    public static HttpProblem MapSourceUnsupported(BackupSourceUnsupported _) => Problem(
        422,
        "backups.unsupported-provider",
        "Service backups are not supported");

    public static HttpProblem MapSourcePlacementUnavailable(BackupSourcePlacementUnavailable _) => Problem(
        422,
        "backups.placement-unavailable",
        "Source placement is unavailable");

    public static HttpProblem MapSourceRevisionChanged(BackupSourceRevisionChanged _) => Problem(
        409,
        "backups.source-revision-changed",
        "Source changed during admission");

    public static HttpProblem MapDestinationPlacementUnavailable(BackupDestinationPlacementUnavailable _) => Problem(
        422,
        "backups.destination-placement-unavailable",
        "Destination placement is unavailable");

    public static HttpProblem MapRetentionInvalid(BackupRetentionInvalid _) => Problem(
        422,
        "backups.backup-positive-retention-required",
        "Backup retention is invalid");

    public static HttpProblem MapDestinationUnavailable(BackupPolicyDestinationUnavailable _) => Problem(
        422,
        "backups.destination-unavailable",
        "Backup destination is unavailable");

    public static HttpProblem MapPolicyNameExists(BackupPolicyNameAlreadyExists _) => Problem(
        409,
        "backups.policy-name-exists",
        "A backup policy with this name exists");

    public static HttpProblem MapPolicySourceBound(BackupPolicySourceAlreadyBound _) => Problem(
        409,
        "backups.source-policy-exists",
        "The service already has a backup policy");

    public static HttpProblem MapPolicyMissing(BackupPolicyMissing _) => Problem(
        404,
        "backups.not-found",
        "Backup policy not found");

    public static HttpProblem MapPolicyRevisionConflict(BackupPolicyRevisionConflict _) => Problem(
        409,
        "backups.revision-conflict",
        "Backup policy changed");

    public static HttpProblem MapPolicyIdentityImmutable(BackupPolicyIdentityImmutable _) => Problem(
        409,
        "backups.policy-identity-immutable",
        "Backup policy identity cannot change");

    public static HttpProblem MapPolicyStopping(BackupPolicyStopping _) => Problem(
        409,
        "backups.policy-stopping",
        "Backup policy is stopping");

    public static HttpProblem MapPolicyGitOwned(BackupPolicyGitOwned _) => Problem(
        409,
        "backups.git-owned",
        "Backup policy is managed by Git");

    private static HttpProblem Problem(
        int status,
        string code,
        string title) => new(status, PoleProblemTypeId.FromCode(code), title, code);
}
