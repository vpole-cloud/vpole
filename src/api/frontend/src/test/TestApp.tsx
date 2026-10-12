import { createRoot } from 'react-dom/client'

// Each interaction test owns its DOM root; query isolation is added by the app provider.
export function createTestApp() {
  const container = document.createElement('div')
  document.body.append(container)
  return { container, root: createRoot(container) }
}
