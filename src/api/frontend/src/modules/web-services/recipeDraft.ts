// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { SourceBuildRecipe } from './buildTypes'

export const fieldsFor = (recipe: SourceBuildRecipe) => ({ dockerfile: recipe.dockerfile })
