import type { SourceBuildRecipe } from './buildTypes'

export const fieldsFor = (recipe: SourceBuildRecipe) => ({ dockerfile: recipe.dockerfile })
