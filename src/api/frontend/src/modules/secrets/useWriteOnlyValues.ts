import { useCallback, useEffect, useState } from 'react'

export interface ValueRow {
  key: string
  value: string
  confirmed?: boolean
}
export function validateValues(rows: ValueRow[]): string {
  if (rows.some((row) => !row.confirmed))
    return 'Enter every value or explicitly choose an empty value.'
  if (
    !rows.length ||
    rows.length > 64 ||
    rows.some((row) => !/^[A-Za-z0-9_.-]{1,128}$/.test(row.key))
  )
    return 'Use 1–64 keys containing letters, digits, underscores, periods or hyphens (up to 128 characters).'
  if (new Set(rows.map((row) => row.key)).size !== rows.length) return 'Each key must be unique.'
  if (rows.reduce((size, row) => size + new TextEncoder().encode(row.value).length, 0) > 65536)
    return 'The complete values must fit within 64 KiB.'
  return ''
}

export function useWriteOnlyValues(keys: string[] = []) {
  const [rows, setRows] = useState<ValueRow[]>(() =>
    keys.length ? keys.map((key) => ({ key, value: '' })) : [{ key: '', value: '' }],
  )
  const [cleared, setCleared] = useState(false)
  const clear = useCallback(() => {
    setRows((rows) => rows.map((row) => ({ ...row, value: '', confirmed: false })))
    setCleared(true)
  }, [])
  return { rows, setRows, cleared, clear }
}

export function useClearOnLeave(clear: () => void, cancel: () => void) {
  useEffect(() => {
    const hide = () => {
      clear()
      cancel()
    }
    const visibility = () => {
      if (document.hidden) hide()
    }
    window.addEventListener('blur', hide)
    document.addEventListener('visibilitychange', visibility)
    return () => {
      window.removeEventListener('blur', hide)
      document.removeEventListener('visibilitychange', visibility)
    }
  }, [clear, cancel])
}
