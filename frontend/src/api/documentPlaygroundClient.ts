import { request } from './http'

export type DocumentParserFormat = 'pdf' | 'docx' | 'xlsx' | 'pptx'
export type DocumentPlaygroundProvider = 'markitdown' | 'pageindex' | DocumentParserFormat
export type ParserWarning = { code: string; message: string }

export function parsePlaygroundDocument(format: DocumentParserFormat, file: File, skipImages: boolean, enrichImages: boolean, signal: AbortSignal) {
  const body = new FormData()
  body.append('file', file)
  body.append('skip_images', String(skipImages))
  body.append('enrich_images', String(enrichImages))
  return request<DocumentEvaluation<{ markdown: string; warnings: ParserWarning[] }>>(`/api/admin/${format}-playground/convert`, { method: 'POST', body, signal, cache: 'no-store' })
}

export interface DocumentEvaluation<T> {
  result: T
  elapsedMs: number
  traceId: string | null
}
export interface PageIndexTree {
  doc_name: string
  structure: unknown[]
  warnings: string[]
  source_line_offset?: number | null
  usage?: {
    model_id: string | null
    input_tokens: number
    output_tokens: number
    total_tokens: number
    model_calls: number
    calls_without_usage: number
  } | null
}
export function convertPlaygroundDocument(file: File, signal: AbortSignal) {
  const body = new FormData()
  body.append('file', file)
  return request<DocumentEvaluation<{ markdown: string }>>('/api/admin/markitdown-playground/convert', { method: 'POST', body, signal, cache: 'no-store' })
}
export function indexPlaygroundDocument(file: File, includeText: boolean, includeSummaries: boolean, signal: AbortSignal) {
  const body = new FormData()
  body.append('file', file)
  body.append('include_text', String(includeText))
  body.append('include_summaries', String(includeSummaries))
  return request<DocumentEvaluation<PageIndexTree>>('/api/admin/pageindex-playground/index', { method: 'POST', body, signal, cache: 'no-store' })
}
