// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Backups.Application.Declarations;

internal closed record BackupDeclarationFailure : IDeclarationFailure;

internal sealed record BackupDeclarationReferenceUnavailable : BackupDeclarationFailure;

internal sealed record BackupDeclarationDestinationNameExists : BackupDeclarationFailure;

internal sealed record BackupDeclarationScopeUnavailable : BackupDeclarationFailure;

internal sealed record BackupDeclarationRegistrationConflict : BackupDeclarationFailure;

internal sealed record BackupDeclarationCredentialGrantDenied : BackupDeclarationFailure;

internal sealed record BackupDeclarationResourceMissing : BackupDeclarationFailure;

internal sealed record BackupDeclarationRevisionConflict : BackupDeclarationFailure;

internal sealed record BackupDeclarationDestinationIdentityImmutable : BackupDeclarationFailure;

internal sealed record BackupDeclarationDestinationInUse : BackupDeclarationFailure;

internal sealed record BackupDeclarationDestinationCleanupPending : BackupDeclarationFailure;

internal sealed record BackupDeclarationSourceUnavailable : BackupDeclarationFailure;

internal sealed record BackupDeclarationSourceUnsupported : BackupDeclarationFailure;

internal sealed record BackupDeclarationSourcePlacementUnavailable : BackupDeclarationFailure;

internal sealed record BackupDeclarationSourceRevisionChanged : BackupDeclarationFailure;

internal sealed record BackupDeclarationDestinationPlacementUnavailable : BackupDeclarationFailure;

internal sealed record BackupDeclarationRetentionInvalid : BackupDeclarationFailure;

internal sealed record BackupDeclarationDestinationUnavailable : BackupDeclarationFailure;

internal sealed record BackupDeclarationPolicyNameExists : BackupDeclarationFailure;

internal sealed record BackupDeclarationSourcePolicyExists : BackupDeclarationFailure;

internal sealed record BackupDeclarationPolicyIdentityImmutable : BackupDeclarationFailure;

internal sealed record BackupDeclarationPolicyStopping : BackupDeclarationFailure;
