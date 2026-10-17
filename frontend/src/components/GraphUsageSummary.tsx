export type GraphModelUsage = {
  modelId: string
  inputTokens: number | null
  outputTokens: number | null
  totalTokens: number | null
}

export function GraphUsageSummary({ usage }: { usage: GraphModelUsage[] }) {
  const tokens = (field: 'inputTokens' | 'outputTokens' | 'totalTokens') => {
    if (!usage.length || usage.some(request => request[field] == null)) {
      return 'Not reported'
    }
    return usage.reduce((sum, request) => sum + (request[field] ?? 0), 0).toLocaleString()
  }
  return <div className="muted" aria-label="Model and token usage">
    <p><strong>Model/deployment:</strong> {[...new Set(usage.map(request => request.modelId))].join(', ') || 'Not reported'}</p>
    <p><strong>Tokens:</strong> {tokens('inputTokens')} input · {tokens('outputTokens')} output · {tokens('totalTokens')} total · {usage.length} requests (including retries)</p>
  </div>
}
