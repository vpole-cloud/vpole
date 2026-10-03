export const postgresSections = ['overview', 'configuration', 'credentials'] as const
export type PostgresSection = typeof postgresSections[number]
