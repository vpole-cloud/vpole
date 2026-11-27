// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

interface SummaryPanelProps {
  label: string
  value: string
  tone?: 'default' | 'positive'
  helper: string
}

export function SummaryPanel({ label, value, tone = 'default', helper }: SummaryPanelProps) {
  return (
    <article className={`summary-panel summary-panel--${tone}`}>
      <p className="summary-panel__label">{label}</p>
      <p className="summary-panel__value">{value}</p>
      <p className="summary-panel__helper">{helper}</p>
    </article>
  )
}
