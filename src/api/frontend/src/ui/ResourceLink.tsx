import type { ReactNode } from 'react'

export function ResourceLink({
  href,
  onNavigate,
  children,
  className = 'ui-resource-link',
}: {
  href: string
  onNavigate: () => void
  children: ReactNode
  className?: string
}) {
  return (
    <a
      className={className}
      href={href}
      onClick={(event) => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey)
          return
        event.preventDefault()
        onNavigate()
      }}
    >
      {children}
    </a>
  )
}
