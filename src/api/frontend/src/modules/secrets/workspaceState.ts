export const secretSections = ['overview', 'rotate', 'bindings', 'projections'] as const
export type SecretSection = (typeof secretSections)[number]
