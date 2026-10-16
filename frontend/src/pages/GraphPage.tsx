import { useEffect, useRef, useState } from 'react'
import { Network, Search, Square, ExternalLink } from 'lucide-react'
import { searchGraph, type GraphSearchResult } from '../api/graphClient'
import { CopyButton, ErrorBanner } from '../components/ui'

const statusMessages: Record<string, string> = {
  NoSeedEntities: 'No graph entities matched this question. Try a specific system, project, or document name.',
  NoAuthorizedEvidence: 'No current relationship evidence was found in documents you can access.',
  Disabled: 'Graph retrieval is disabled for this tenant.',
  NoUser: 'Your identity could not be resolved. Sign in again.',
  FellBack: 'Graph retrieval could not complete. Try again or use the Search page.',
}

export default function GraphPage() {
  const [query, setQuery] = useState('')
  const [top, setTop] = useState(10)
  const [result, setResult] = useState<GraphSearchResult | null>(null)
  const [error, setError] = useState('')
  const [running, setRunning] = useState(false)
  const [predicate, setPredicate] = useState('')
  const [selected, setSelected] = useState(0)
  const controller = useRef<AbortController | null>(null)
  useEffect(() => () => controller.current?.abort(), [])
  const chunks = result?.chunks.filter(chunk => !predicate || chunk.predicate === predicate) ?? []
  const detail = chunks[selected]
  const sourceUrl = detail?.hit.webUrl && /^https?:\/\//i.test(detail.hit.webUrl) ? detail.hit.webUrl : null

  const run = async () => {
    if (controller.current || !query.trim()) {
      return
    }
    const pending = new AbortController()
    controller.current = pending
    setRunning(true)
    setError('')
    setResult(null)
    setPredicate('')
    setSelected(0)
    try {
      const response = await searchGraph(query, top, pending.signal)
      if (!pending.signal.aborted) {
        setResult(response)
      }
    } catch (cause) {
      setError(pending.signal.aborted ? 'Search cancelled.' : cause instanceof Error ? cause.message : 'Graph search failed.')
    } finally {
      controller.current = null
      setRunning(false)
    }
  }

  return <section className="card graph-page">
    <div className="card-head"><h2><Network size={18} />Graph</h2></div>
    <div className="card-body stack">
      <p className="muted">AI identifies the entities and relationships in your question, then searches for supporting evidence in documents you can access.</p>
      <form className="graph-search-form" onSubmit={event => { event.preventDefault(); void run() }}>
        <label className="graph-query">Question<input value={query} maxLength={2000} disabled={running} onChange={event => setQuery(event.target.value)} placeholder="What systems depend on the customer portal?" required /></label>
        <label>Search breadth<select value={top} disabled={running} onChange={event => setTop(Number(event.target.value))}>
          {[5, 10, 20].map(value => <option key={value} value={value}>{value}</option>)}
        </select></label>
        <button className="primary" type="submit" disabled={running || !query.trim()}><Search size={15} />Search graph</button>
        {running && <button type="button" onClick={() => controller.current?.abort()}><Square size={15} />Cancel</button>}
      </form>
      {error && <ErrorBanner message={error} />}
      <div aria-live="polite" role="status">
        {running ? 'Searching graph evidence…' : result ? statusMessages[result.status] ?? `${result.chunks.length} evidence chunks found · ${result.elapsedMs.toLocaleString()} ms` : 'Enter a relationship question to begin.'}
      </div>
      {!!result?.chunks.length && <>
        <label className="graph-filter">Relationship<select aria-label="Relationship" value={predicate} onChange={event => { setPredicate(event.target.value); setSelected(0) }}>
          <option value="">All relationships</option>
          {[...new Set(result.chunks.map(chunk => chunk.predicate))].sort().map(value => <option key={value} value={value}>{value.replaceAll('_', ' ')}</option>)}
        </select></label>
        <div className="graph-results">
          <div className="graph-result-list" aria-label="Relationship evidence">
            {chunks.map((chunk, index) => <button type="button" key={chunk.hit.id} className={`graph-result${selected === index ? ' selected' : ''}`} aria-pressed={selected === index} onClick={() => setSelected(index)}>
              <strong>{chunk.hit.name}</strong>
              <span>{chunk.predicate.replaceAll('_', ' ')} · {chunk.depth} {chunk.depth === 1 ? 'hop' : 'hops'}{chunk.disputed ? ' · Disputed' : ''}</span>
              <small className="muted">Chunk {chunk.hit.chunkNumber} · {chunk.hit.path ?? 'Indexed document'}</small>
            </button>)}
          </div>
          {detail && <article className="graph-evidence decision-panel">
            <div className="decision-panel-heading"><h3>{detail.hit.name}</h3><CopyButton value={detail.hit.content} /></div>
            <div className="graph-evidence-body">
              <p><strong>{detail.predicate.replaceAll('_', ' ')}</strong> · {detail.depth} {detail.depth === 1 ? 'hop' : 'hops'}</p>
              {detail.disputed && <p role="note">This relationship is disputed. Review the source evidence before relying on it.</p>}
              {sourceUrl && <a href={sourceUrl} target="_blank" rel="noopener noreferrer"><ExternalLink size={14} />Open source document</a>}
              <pre>{detail.hit.content}</pre>
            </div>
          </article>}
        </div>
        <small className="muted">Results are bounded by Graph RAG traversal limits. Each card shows a verified relationship’s supporting chunk; this is not an exhaustive graph listing.</small>
      </>}
    </div>
  </section>
}
