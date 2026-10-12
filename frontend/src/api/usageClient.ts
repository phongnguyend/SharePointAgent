import { request, query } from './http'
import type {
  ImageDescriptionUsageReport,
  SystemOneUsageReport,
  SystemOneUsageFilter,
  ImageDescriptionUsageFilter,
  VoiceUsageFilter,
  VoiceUsageReport,
  ContentSafetyUsageReport,
  ChatUsageFilter,
  ChatUsageReport,
  EmbeddingUsageReport,
  EmbeddingUsageFilter,
} from './types'

export const getVoiceUsage = (filters: VoiceUsageFilter, skip: number, signal?: AbortSignal) =>
  request<VoiceUsageReport>(`/api/usage/transcriptions${query({ ...filters, user: filters.user.trim() || undefined, skip, top: 25 })}`, { signal })

export const getImageDescriptionUsage = (filters: ImageDescriptionUsageFilter, skip: number, signal?: AbortSignal) =>
  request<ImageDescriptionUsageReport>(`/api/usage/image-descriptions${query({ ...filters, user: filters.user.trim() || undefined, attachmentId: filters.attachmentId || undefined, skip, top: 25 })}`, { signal })

export const getContentSafetyUsage = (filters: { from: string; to: string; status: string; operation: string }, skip: number, signal?: AbortSignal) =>
  request<ContentSafetyUsageReport>(`/api/usage/content-safety${query({ ...filters, skip, top: 25 })}`, { signal })

export const getChatTokenUsage = (filters: ChatUsageFilter, skip: number, signal?: AbortSignal) =>
  request<ChatUsageReport>(`/api/usage/tokens${query({ ...filters, questionId: filters.questionId || undefined, skip, top: 25 })}`, { signal })

export const getEmbeddingUsage = (filters: EmbeddingUsageFilter, skip: number, signal?: AbortSignal) =>
  request<EmbeddingUsageReport>(`/api/usage/embeddings${query({ ...filters, skip, top: 25 })}`, { signal })

export const getSystemOneUsage = (filters: SystemOneUsageFilter, skip: number, signal?: AbortSignal) =>
  request<SystemOneUsageReport>(`/api/usage/systemone${query({ ...filters, skip, top: 25 })}`, { signal })
