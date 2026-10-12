import type { ResourceGroupInput } from './types'

export const resourceGroupSignature = (value: ResourceGroupInput) =>
  JSON.stringify({ name: value.name.trim(), description: value.description.trim(), tags: [...new Set(value.tags)].sort() })

export function resourceGroupInput(name: string, description: string, tags: string): ResourceGroupInput {
  return { name: name.trim(), description: description.trim(), tags: [...new Set(tags.split(',').map((tag) => tag.trim()).filter(Boolean))] }
}
