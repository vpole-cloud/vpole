export const retryOperationForStatus = (status: string): 'Apply' | 'Delete' =>
  status.toLowerCase().includes('delete') ? 'Delete' : 'Apply'
