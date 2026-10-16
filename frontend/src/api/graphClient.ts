import { request } from './http'
import type { SearchQueryHit } from './types'

export type GraphRelatedChunk = { hit: SearchQueryHit; predicate: string; depth: number; disputed: boolean }
export type GraphQueryPlan = { entities: string[]; relationshipTypes: string[] }
export type GraphSearchResult = { status: string; chunks: GraphRelatedChunk[]; plan: GraphQueryPlan; elapsedMs: number }
export const searchGraph = (query: string, top: number, signal: AbortSignal) => request<GraphSearchResult>('/api/graph/search', {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ query, top }), signal,
})
