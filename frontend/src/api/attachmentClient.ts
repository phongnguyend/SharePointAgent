import { request, query, downloadBlob } from './http'
import type {
  PagedResult,
  AttachmentFileRecord,
} from './types'

export function downloadAttachmentFile(id: string, signal?: AbortSignal): Promise<Blob> {
  return downloadBlob(`/api/attachment-files/${encodeURIComponent(id)}/download`, signal)
}

export function getAttachmentFileMarkdown(id: string, signal?: AbortSignal): Promise<{ markdown: string }> {
  return request<{ markdown: string }>(`/api/attachment-files/${encodeURIComponent(id)}/markdown`, { signal })
}

export function convertAttachmentFileToMarkdown(id: string, signal?: AbortSignal): Promise<{ markdown: string }> {
  return request(`/api/attachment-files/${encodeURIComponent(id)}/convert-to-markdown`, { method: 'POST', signal })
}

export async function uploadAttachmentFile(file: File, signal?: AbortSignal): Promise<AttachmentFileRecord> {
  const body = new FormData()
  body.append('file', file)
  return request<AttachmentFileRecord>('/api/attachment-files', { method: 'POST', body, signal })
}

export function describeAttachmentImage(id: string, signal?: AbortSignal): Promise<{ text: string }> {
  return request(`/api/attachment-files/${encodeURIComponent(id)}/describe-image`, { method: 'POST', signal })
}

export async function uploadOrphanAttachmentFile(file: File, signal?: AbortSignal): Promise<AttachmentFileRecord> {
  const body = new FormData()
  body.append('file', file)
  return request<AttachmentFileRecord>('/api/attachment-files?index=false', { method: 'POST', body, signal })
}

export function extractAttachmentText(id: string, signal?: AbortSignal): Promise<{ text: string }> {
  return request(`/api/attachment-files/${encodeURIComponent(id)}/extract-text`, { method: 'POST', signal })
}

export function getAttachmentOptions(signal?: AbortSignal): Promise<{ allowedFileExtensions: string[]; maxFileBytes: number; textFileExtensions: string[]; imageFileExtensions: string[]; imageDescriptionExtensions: string[]; imageTextExtensions: string[] }> {
  return request('/api/attachment-files/options', { signal })
}

export function listAttachmentFiles(
  options: { search?: string; skip?: number; top?: number },
  signal?: AbortSignal,
): Promise<PagedResult<AttachmentFileRecord>> {
  return request<PagedResult<AttachmentFileRecord>>(`/api/attachment-files${query({ ...options })}`, { signal })
}

export function reindexAttachmentFile(id: string): Promise<AttachmentFileRecord> {
  return request<AttachmentFileRecord>(`/api/attachment-files/${encodeURIComponent(id)}/reindex`, { method: 'POST' })
}

export function deleteOrphanAttachmentFile(id: string): Promise<{ deleted: string }> {
  return request<{ deleted: string }>(`/api/attachment-files/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
}
