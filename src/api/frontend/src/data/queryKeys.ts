// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// Include every server filter. Absent optional scope IDs have one canonical identity.
export const eligibilityKeys = {
  scopes: (groupId: string | undefined) => ['resource-groups', 'scopes', groupId ?? null] as const,
  targets: (
    resourceType: string | undefined,
    isolationScopeId: string | null | undefined,
    publicIngress: boolean,
  ) =>
    [
      'eligibility',
      'deployment-targets',
      resourceType ?? null,
      isolationScopeId ?? null,
      publicIngress,
    ] as const,
  compute: (resourceType: string, targetId: string | undefined) =>
    ['eligibility', 'compute-profiles', resourceType, targetId ?? null] as const,
  storage: (resourceType: string, targetId: string | undefined) =>
    ['eligibility', 'storage-profiles', resourceType, targetId ?? null] as const,
}
