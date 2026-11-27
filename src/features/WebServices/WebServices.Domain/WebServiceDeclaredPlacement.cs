// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.WebServices.Domain;

// Names are the user's desired references; IDs pin their resolved identities across retries.
public sealed record WebServiceDeclaredPlacement(
    string DeploymentTargetName,
    string DeploymentTargetId,
    string ComputeProfileName,
    string ComputeProfileId
);
