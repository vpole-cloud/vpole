import { useLayoutEffect, useRef, type ReactNode } from 'react'

export interface ConsoleTab {
  id: string
  label: string
  dirty?: boolean
  onClose?: () => void
}

export function Tabs({ id, label, tabs, activeId, onSelect, variant = 'workspace' }:
  { id: string; label: string; tabs: readonly ConsoleTab[]; activeId: string; onSelect: (id: string) => void; variant?: 'workspace' | 'section' }) {
  const list = useRef<HTMLDivElement>(null)
  const layout = tabs.map((tab) => `${tab.id}:${tab.label}:${Boolean(tab.dirty)}`).join('|')
  useLayoutEffect(() => {
    const element = list.current
    const tab = element?.querySelector<HTMLButtonElement>('[role="tab"][aria-selected="true"]')?.parentElement
    if (!element || !tab) return
    const listBounds = element.getBoundingClientRect()
    const tabBounds = tab.getBoundingClientRect()
    if (tabBounds.left < listBounds.left) element.scrollLeft -= listBounds.left - tabBounds.left
    else if (tabBounds.right > listBounds.right) element.scrollLeft += tabBounds.right - listBounds.right
  }, [activeId, layout])
  return <div ref={list} className={`console-tabs console-tabs--${variant}`} role="tablist" aria-label={label}>
    {tabs.map((tab, index) => <div className={`console-tab${tab.id === activeId ? ' is-active' : ''}`} role="presentation" key={tab.id}>
      <button type="button" role="tab" id={`${id}-tab-${tab.id}`} aria-controls={`${id}-panel-${tab.id}`}
        aria-selected={tab.id === activeId} tabIndex={tab.id === activeId ? 0 : -1}
        aria-label={`${tab.label}${tab.dirty ? ', unsaved changes' : ''}`} title={tab.label}
        onClick={() => onSelect(tab.id)} onKeyDown={(event) => {
          if (event.key === 'Delete' && tab.onClose) {
            event.preventDefault()
            tab.onClose()
            return
          }
          const next = event.key === 'ArrowRight' ? (index + 1) % tabs.length :
            event.key === 'ArrowLeft' ? (index + tabs.length - 1) % tabs.length :
              event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : undefined
          if (next === undefined) return
          event.preventDefault()
          onSelect(tabs[next].id)
          event.currentTarget.closest('[role="tablist"]')!
            .querySelectorAll<HTMLButtonElement>('[role="tab"]')[next].focus()
        }}><span>{tab.label}</span>{tab.dirty && <span className="console-tab__dirty" aria-hidden="true">●</span>}</button>
      {tab.onClose && <button className="console-tab__close" type="button" tabIndex={-1}
        aria-label={`Close ${tab.label}`} title={`Close ${tab.label} (Delete)`} onClick={tab.onClose}>×</button>}
    </div>)}
  </div>
}

export function TabPanel({ id, tabId, activeId, children }:
  { id: string; tabId: string; activeId: string; children: ReactNode }) {
  return <div role="tabpanel" id={`${id}-panel-${tabId}`} aria-labelledby={`${id}-tab-${tabId}`}
    hidden={tabId !== activeId} tabIndex={0}>{children}</div>
}
