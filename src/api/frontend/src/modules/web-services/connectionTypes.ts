export interface ResourceConnection {
  id: string
  name: string
  sourceResourceType: string
  sourceResourceId: string
  destinationResourceType: string
  destinationResourceId: string
  endpointId: string
  status: string
  generation: number
  targets: {
    targetId: string
    phase: 'Apply' | 'Cleanup'
    status: string
    host: string
    port: number
    failureCode: string | null
  }[]
}
