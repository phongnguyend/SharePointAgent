import { HardDrive } from 'lucide-react'
import { formatBytes } from '../lib/format'

export function AttachmentStorageUsage({ used, limit }: { used: number; limit: number | null }) {
  const full = limit !== null && used >= limit
  return <div className="attachment-storage-usage">
    <span className="row"><HardDrive size={14} aria-hidden="true" />{formatBytes(used)} used / {limit === null ? 'Unlimited' : formatBytes(limit)}</span>
    {limit !== null && <progress aria-label="Attachment storage used" max={limit || 1} value={limit === 0 ? 1 : Math.min(used, limit)} />}
    {full && <span className="badge warning">{used > limit! ? 'Over limit' : limit === 0 ? 'Uploads disabled' : 'Limit reached'}</span>}
  </div>
}
