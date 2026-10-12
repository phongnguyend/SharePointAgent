import { request } from './http'

export type DecisionProvider = 'jev' | 'microsoft-decision'

export interface DecisionEvaluation {
  result: {
    model: string
    answers: Record<string, unknown>
    usage: { input_tokens: number; output_tokens: number }
  }
  elapsedMs: number
  traceId: string | null
}

export const evaluateDecision = (provider: DecisionProvider, input: unknown, signal?: AbortSignal) =>
  request<DecisionEvaluation>(`/api/admin/decision-playground/${provider}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input), signal, cache: 'no-store',
  })
