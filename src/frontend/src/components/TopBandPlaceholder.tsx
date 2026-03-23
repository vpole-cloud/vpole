export function TopBandPlaceholder() {
  return (
    <div className="top-band">
      <div>
        <p className="top-band__label">Reserved global header</p>
        <p className="top-band__hint">
          This band stays free for future search, breadcrumbs, and account
          actions.
        </p>
      </div>
      <span className="top-band__badge">Desktop preview</span>
    </div>
  )
}
