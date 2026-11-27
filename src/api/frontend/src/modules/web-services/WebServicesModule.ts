// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { AppModule, AppCommand, AppServices } from '../../shell/contracts'
import { lazy } from 'react'
const WebServicesView = lazy(() =>
  import('./WebServicesView').then((module) => ({ default: module.WebServicesView })),
)

export const webServicesModule: AppModule = {
  id: 'web-services',
  views: [
    { id: 'web-services', label: 'Web services', component: WebServicesView, access: 'tenant' },
  ],
  resourceTypes: [{ id: 'pole/web-service', label: 'Web service', viewId: 'web-services' }],
  commands: [
    {
      id: 'web-services.create',
      label: 'Create web service',
      viewId: 'web-services',
      surface: 'palette',
      available: ({ tenant }) => tenant.canManage,
      run: ({ navigation }) =>
        navigation.openResource({ resourceTypeId: 'pole/web-service', resourceId: 'create' }),
    },
    ...(['configuration', 'deployments', 'builds'] as const).map((section) => ({
      id: `web-services.${section}`,
      label: `Open service ${section}`,
      viewId: 'web-services',
      surface: 'palette' as const,
      available: ({ location, tenant }: Parameters<NonNullable<AppCommand['available']>>[0]) => {
        const id = new URLSearchParams(location.search).get('resource')
        return (
          location.viewId === 'web-services' &&
          Boolean(id && !['catalog', 'create'].includes(id)) &&
          (section !== 'configuration' || tenant.canManage)
        )
      },
      run: ({ navigation }: AppServices) =>
        navigation.openResource({
          resourceTypeId: 'pole/web-service',
          resourceId: new URLSearchParams(navigation.search).get('resource')!,
          section,
        }),
    })),
    {
      id: 'web-services.copy-link',
      label: 'Copy service link',
      viewId: 'web-services',
      surface: 'palette',
      available: ({ location }) =>
        location.viewId === 'web-services' &&
        Boolean(
          new URLSearchParams(location.search).get('resource') &&
          !['catalog', 'create'].includes(new URLSearchParams(location.search).get('resource')!),
        ),
      run: ({ navigation, notifications }) => {
        const href = navigation.resourceHref({
          resourceTypeId: 'pole/web-service',
          resourceId: new URLSearchParams(navigation.search).get('resource')!,
        })
        if (!navigator.clipboard) {
          notifications.publish(
            'Copy is unavailable in this browser. Use the resource link.',
            'error',
          )
          return
        }
        void navigator.clipboard
          .writeText(new URL(href, window.location.origin).href)
          .then(() => notifications.publish('Service link copied.'))
          .catch(() => notifications.publish('Unable to copy this link.', 'error'))
      },
    },
  ],
}
