import { useSyncExternalStore } from 'react'

let pageActive = true
const subscribe = (listener: () => void) => {
  const hide = () => {
    pageActive = false
    listener()
  }
  const show = () => {
    pageActive = true
    listener()
  }
  document.addEventListener('visibilitychange', listener)
  window.addEventListener('pagehide', hide)
  window.addEventListener('pageshow', show)
  return () => {
    document.removeEventListener('visibilitychange', listener)
    window.removeEventListener('pagehide', hide)
    window.removeEventListener('pageshow', show)
  }
}
export const isPageVisible = () => pageActive && !document.hidden
export const usePageVisible = () => useSyncExternalStore(subscribe, isPageVisible)
