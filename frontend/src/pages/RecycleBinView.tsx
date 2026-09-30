import { useState } from 'react'
import { ExternalLink, RefreshCw, Trash2 } from 'lucide-react'
import { browseRecycleBin } from '../api/client'
import { Empty, ErrorBanner, LoadingBar } from '../components/ui'
import { formatBytes, formatDateTime } from '../lib/format'
import { useAsync } from '../lib/useAsync'

const pageSize = 50

export default function RecycleBinView() {
  const listing = useAsync(browseRecycleBin, [])
  const [filter, setFilter] = useState('')
  const [page, setPage] = useState(0)
  const matching = (listing.data?.items ?? []).filter(item =>
    `${item.name} ${item.deletedFromLocation ?? ''}`.toLocaleLowerCase().includes(filter.toLocaleLowerCase()))
  const currentPage = Math.min(page, Math.max(0, Math.ceil(matching.length / pageSize) - 1))
  const visible = matching.slice(currentPage * pageSize, (currentPage + 1) * pageSize)

  return <div className="browse-page">
    <div className="page-head">
      <div><h1>Recycle bin <span className="badge">Preview</span></h1>
        <p>Deleted items from {listing.data?.siteName ?? 'the configured SharePoint site'}, across all its document libraries.</p></div>
      <button disabled={listing.loading} onClick={listing.reload}><RefreshCw size={15} />Refresh</button>
    </div>
    <p className="browse-help">View deleted items here. To restore items or manage the second-stage recycle bin, open SharePoint.</p>
    {listing.data?.recycleBinUrl && <a href={listing.data.recycleBinUrl} target="_blank" rel="noreferrer" className="browse-recycle-link"><ExternalLink size={15} />Open recycle bin in SharePoint</a>}
    {listing.error && <ErrorBanner message={listing.error} onRetry={listing.reload} />}
    <section className="browse-panel" aria-label="Deleted items" aria-busy={listing.loading}>
      <div className="browse-toolbar">
        <label className="browse-field">Filter deleted items
          <input type="search" placeholder="Name or original location…" value={filter} onChange={event => {
            setFilter(event.target.value)
            setPage(0)
          }} />
        </label>
        <span className="badge">Read-only</span>
      </div>
      <LoadingBar active={listing.loading} />
      {listing.loading && <p className="browse-help" role="status">Loading deleted items…</p>}
      {!listing.loading && !listing.error && listing.data && <>
        <div className="browse-table-wrap"><table className="browse-table">
          <thead><tr><th>Name</th><th>Original location</th><th>Deleted</th><th>Size</th></tr></thead>
          <tbody>{visible.map(item => <tr key={item.id}>
            <td><div className="browse-name"><Trash2 size={17} /><span>{item.name}</span></div></td>
            <td>{item.deletedFromLocation || '—'}</td>
            <td>{formatDateTime(item.deletedDateTime)}</td><td>{formatBytes(item.size)}</td>
          </tr>)}</tbody>
        </table></div>
        {matching.length === 0 && <Empty title={filter ? 'No matching deleted items' : 'The recycle bin is empty'}
          detail={filter ? 'Try another name or location.' : 'No deleted items were returned for this site.'} icon={<Trash2 size={26} />} />}
        <div className="browse-toolbar">
          <span role="status">{matching.length === 0 ? '0' : `${currentPage * pageSize + 1}–${Math.min((currentPage + 1) * pageSize, matching.length)}`} of {matching.length} items</span>
          <div className="browse-tools">
            <button disabled={currentPage === 0} onClick={() => setPage(currentPage - 1)}>Previous</button>
            <button disabled={(currentPage + 1) * pageSize >= matching.length} onClick={() => setPage(currentPage + 1)}>Next</button>
          </div>
        </div>
      </>}
    </section>
  </div>
}
