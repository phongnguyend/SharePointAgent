import { request } from './http'
import type {
  SearchMode,
  SearchPayload,
  SearchQueryResults,
  TimedSearch,
} from './types'

export function search(
  mode: SearchMode,
  payload: SearchPayload,
  signal?: AbortSignal,
): Promise<SearchQueryResults> {
  return request<SearchQueryResults>(`/api/search/${mode}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...payload, userId: payload.userId || null }),
    signal,
  })
}

/**
 * Runs one strategy and keeps its timing and any failure alongside the results, so a comparison run
 * reports the strategy that failed instead of losing the two that succeeded.
 */
export async function timedSearch(
  mode: SearchMode,
  payload: SearchPayload,
  signal?: AbortSignal,
): Promise<TimedSearch> {
  const startedAt = performance.now()
  try {
    const results = await search(mode, payload, signal)
    return { mode, results, error: null, elapsedMs: performance.now() - startedAt }
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause
    }
    return {
      mode,
      results: null,
      error: cause instanceof Error ? cause.message : String(cause),
      elapsedMs: performance.now() - startedAt,
    }
  }
}
