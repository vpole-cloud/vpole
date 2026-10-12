import type { SourceBuildRecipe } from './buildTypes'

export const fieldsFor = (recipe: SourceBuildRecipe) => ({ sourceRoot: recipe.sourceRoot, manifestPath: recipe.manifestPath,
  commands: recipe.commands.join('\n'), startCommand: recipe.startCommand ?? '', dockerfile: recipe.dockerfile })
