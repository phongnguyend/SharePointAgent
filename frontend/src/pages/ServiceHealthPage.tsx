import { useEffect, useState } from 'react'
import { Activity, CheckCircle2, CircleAlert, RefreshCw } from 'lucide-react'
import { getServiceHealth } from '../api/client'
import { ErrorBanner, LoadingBar } from '../components/ui'
import { useAsync } from '../lib/useAsync'

const labels = { healthy: 'Healthy', unhealthy: 'Unhealthy', 'not-configured': 'Not configured', misconfigured: 'Configuration error', degraded: 'Degraded', unknown: 'Unknown' }

const timestamp = (value?: string | null) => value ? new Date(value).toLocaleString() : 'Not recorded'

export default function ServiceHealthPage() {
  const [autoRefresh, setAutoRefresh] = useState(true)
  const health = useAsync(getServiceHealth, [])

  useEffect(() => {
    if (!autoRefresh || health.loading) {
      return
    }
    const timer = window.setTimeout(() => {
      health.reload()
    }, 30000)
    return () => window.clearTimeout(timer)
  }, [autoRefresh, health.loading, health.reload])

  return <section className="card">
    <div className="card-head service-health-heading">
      <h2><Activity size={18} />Service health</h2>
      <label className="service-health-refresh">
        <input type="checkbox" checked={autoRefresh} onChange={event => setAutoRefresh(event.target.checked)} />
        Refresh every 30 seconds
      </label>
      <button className="primary" type="button" disabled={health.loading} onClick={health.reload}>
        <RefreshCw size={15} />{health.loading ? 'Checking…' : 'Check now'}
      </button>
    </div>
    <LoadingBar active={health.loading} />
    <div className="card-body stack">
      <p>Checks API connectivity and the Background worker's persisted heartbeat. API checks do not verify conversion, indexing, or model credentials.</p>
      {health.error && <ErrorBanner message={`${health.error} Previous results may be out of date.`} />}
      <div className="service-health-grid" aria-live="polite" aria-busy={health.loading}>
        {(health.data ?? []).map(service => <article className="service-health-item" key={service.name}>
          <div className="row">
            <h3>{service.name}</h3>
            <span className={`badge ${service.status === 'healthy' ? 'good' : service.status === 'unhealthy' ? 'critical' : 'warning'}`}>
              {service.status === 'healthy' ? <CheckCircle2 size={14} /> : <CircleAlert size={14} />}
              {labels[service.status]}
            </span>
          </div>
          <p>{service.message}</p>
          <div className="service-health-meta">
            {service.name !== 'Background' && <span>Response: {service.responseTimeMs === null ? '—' : `${service.responseTimeMs} ms`}</span>}
            <span>Checked: <time dateTime={service.checkedAtUtc}>{new Date(service.checkedAtUtc).toLocaleString()}</time></span>
          </div>
          {service.name === 'Background' && <dl className="detail-grid">
            <dt>Last heartbeat</dt><dd>{timestamp(service.lastHeartbeatUtc)}</dd>
            <dt>Last successful sync</dt><dd>{timestamp(service.lastSyncSucceededUtc)}</dd>
            <dt>Last failure</dt><dd>{timestamp(service.lastFailureUtc)}</dd>
            <dt>Subscription expiry</dt><dd>{timestamp(service.subscriptionExpiresUtc)}</dd>
          </dl>}
        </article>)}
      </div>
    </div>
  </section>
}
