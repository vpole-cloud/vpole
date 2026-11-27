// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

export const secretSections = ['overview', 'rotate', 'bindings', 'projections'] as const
export type SecretSection = (typeof secretSections)[number]
