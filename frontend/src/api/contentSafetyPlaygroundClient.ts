import { request } from './http'

export type SafetyOptions = { configured: boolean; maxCharacters: number }
export type SafetyAssessment = {
  assessmentId: string; status: string; characterCount: number; elapsedMs: number; traceId: string | null
  categories: { category: string; severity: number; threshold: number }[]
}
export const getSafetyOptions = (signal?: AbortSignal) => request<SafetyOptions>('/api/admin/content-safety-playground/options', { signal })
export const evaluateSafety = (text: string, signal?: AbortSignal) => request<SafetyAssessment>('/api/admin/content-safety-playground', {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text }), signal,
})
