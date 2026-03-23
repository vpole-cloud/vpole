import type { ReactNode } from 'react'

interface AppShellProps {
  sideNav: ReactNode
  topBand: ReactNode
  children: ReactNode
}

export function AppShell({ sideNav, topBand, children }: AppShellProps) {
  return (
    <div className="app-shell">
      <aside className="app-shell__side-nav">{sideNav}</aside>
      <div className="app-shell__workspace">
        <div className="app-shell__top-band">{topBand}</div>
        <main className="app-shell__content">{children}</main>
      </div>
    </div>
  )
}
