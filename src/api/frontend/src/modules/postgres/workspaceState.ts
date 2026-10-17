export const postgresSections = ['overview', 'operations', 'configuration', 'credentials'] as const
export type PostgresSection = (typeof postgresSections)[number]
