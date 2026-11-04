import { ResourceWorkspace } from '../../ui/ResourceWorkspace'
import { sections } from './model'
import { GitOpsCatalog } from './GitOpsCatalog'
import { NewSource } from './NewSource'
import { GitOpsResourceView } from './GitOpsResourceView'
export function GitOpsView() {
  return (
    <ResourceWorkspace
      id="gitops"
      label="GitOps sources"
      newLabel="New source"
      sections={sections}
      defaultSection="overview"
      Catalog={GitOpsCatalog}
      Create={NewSource}
      Resource={GitOpsResourceView}
    />
  )
}
