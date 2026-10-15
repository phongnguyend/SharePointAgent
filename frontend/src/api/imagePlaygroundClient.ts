import { request } from './http'

export type ImageOperation = 'describe' | 'extract'
export interface ImageEvaluation {
  text: string
  modelId: string | null
  usage: { inputTokens: number; outputTokens: number; totalTokens: number } | null
  elapsedMs: number
  traceId: string | null
}
export function evaluateImage(operation: ImageOperation, file: File, signal: AbortSignal) {
  const body = new FormData()
  body.append('file', file)
  return request<ImageEvaluation>(`/api/admin/image-playground/${operation}`, { method: 'POST', body, signal, cache: 'no-store' })
}
