// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

export const postgresSections = ['overview', 'operations', 'configuration', 'credentials'] as const
export type PostgresSection = (typeof postgresSections)[number]
