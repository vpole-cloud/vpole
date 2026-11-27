// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { WebServiceSpec } from './types'

export function imageDescription(spec: WebServiceSpec): string {
  if (!spec.image)
    return spec.imageAuthority?.mode === 'UnboundSourceBuild'
      ? 'Awaiting build configuration'
      : 'Awaiting first artifact'
  const repository = `${spec.image.server}/${spec.image.name}`
  return spec.imageAuthority?.digest
    ? `${repository}@${spec.imageAuthority.digest}`
    : `${repository}:${spec.image.tag}`
}

export const hasBuildImageAuthority = (spec: WebServiceSpec) =>
  Boolean(spec.imageAuthority && spec.imageAuthority.mode !== 'Direct')
