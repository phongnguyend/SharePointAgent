import { Files, HardDrive, RefreshCw, Unlink, UserRound } from 'lucide-react'
import { getSystemAttachmentStorage } from '../api/client'
import { useAsync } from '../lib/useAsync'
import { formatBytes, formatNumber } from '../lib/format'
import { ErrorBanner, LoadingBar, StatTile } from './ui'

export function SystemAttachmentStorage({ refreshKey }: { refreshKey?: unknown }) {
  const storage = useAsync(signal => getSystemAttachmentStorage(signal), [refreshKey])
  return <section className="card" aria-label="System attachment storage">
    <div className="card-head">
      <h2><HardDrive size={16} />System attachment storage</h2>
      <button onClick={storage.reload} disabled={storage.loading}><RefreshCw size={14} aria-hidden="true" />Refresh storage</button>
    </div>
    <div className="card-body stack">
      <LoadingBar active={storage.loading} />
      {storage.error && <ErrorBanner message={storage.error} onRetry={storage.reload} />}
      {storage.data && <div className="system-storage-grid">
        <StatTile label="Total used" value={formatBytes(storage.data.usedBytes)} icon={<HardDrive size={14} />} hint="Across all users and indexing states" />
        <StatTile label="Stored files" value={formatNumber(storage.data.fileCount)} icon={<Files size={14} />} hint="Each uploaded file counted once" />
        <StatTile label="Unlinked files" value={formatBytes(storage.data.orphanBytes)} icon={<Unlink size={14} />} hint="Not attached to a conversation" />
        <StatTile label="Unassigned storage" value={formatBytes(storage.data.unassignedBytes)} icon={<UserRound size={14} />} hint="Legacy files without a creator" />
      </div>}
      <p style={{ margin: 0, color: 'var(--text-muted)', fontSize: 12 }}>Original attachment files tracked by this application. Unlinked and unassigned storage are included in the total and may overlap. Azure capacity and search-index storage are not included.</p>
    </div>
  </section>
}
