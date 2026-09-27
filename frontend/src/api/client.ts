import { authorizedFetch, API_BASE } from '../auth'
import type {
  ChatUsageFilter,
  ChatUsageReport,
  EmbeddingUsageReport,
  EmbeddingUsageFilter,
  AppUser,
  SystemAttachmentStorage,
  AppUserInput,
  AppUserStorageInput,
  AgentDefinition,
  ChatConversation,
  ChatFeedback,
  ChatStreamEvent,
  ChatThread,
  ChatTurnResult,
  DeltaStateRow,
  FeedbackPage,
  IndexStateSummary,
  IndexedFileQuery,
  IndexedFileRow,
  PagedResult,
  SearchMode,
  SearchPayload,
  SearchQueryResults,
  SubscriptionOverview,
  SubscriptionView,
  UpdateSubscriptionResult,
  TimedSearch,
  AttachmentFileRecord,
} from './types'

/** Empty by default, so requests go to the dev server's /api proxy on this same origin. */
const BASE_URL = API_BASE

export const getChatTokenUsage = (filters: ChatUsageFilter, skip: number, signal?: AbortSignal) =>
  request<ChatUsageReport>(`/api/usage/tokens${query({ ...filters, questionId: filters.questionId || undefined, skip, top: 25 })}`, { signal })

export const getEmbeddingUsage = (filters: EmbeddingUsageFilter, skip: number, signal?: AbortSignal) =>
  request<EmbeddingUsageReport>(`/api/usage/embeddings${query({ ...filters, skip, top: 25 })}`, { signal })

export const getCurrentUser = (signal?: AbortSignal) => request<AppUser>('/api/auth/me', { signal })
export const saveUserTokenLimit = (id: string, input: { monthlyTokenLimit: number | null; concurrencyStamp: string }) =>
  request<AppUser>(`/api/users/${encodeURIComponent(id)}/tokens`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input),
  })
export const saveUserStorage = (id: string, input: AppUserStorageInput) => request<AppUser>(`/api/users/${encodeURIComponent(id)}/storage`, {
  method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input),
})
export const getSystemAttachmentStorage = (signal?: AbortSignal) => request<SystemAttachmentStorage>('/api/storage/attachments', { signal })
export const listUsers = (search: string, skip: number, top: number, signal?: AbortSignal) =>
  request<PagedResult<AppUser>>(`/api/users${query({ search, skip, top })}`, { signal })
export const saveUser = (id: string | null, input: AppUserInput) => request<AppUser>(id ? `/api/users/${encodeURIComponent(id)}` : '/api/users', {
  method: id ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input),
})

export function getSensitivityLabels(signal?: AbortSignal): Promise<Record<string, string>> {
  return request<Record<string, string>>('/api/sensitivity-labels', { signal })
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number | null,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

/**
 * The message to show for a failure. Minimal-API validation failures come back as `{ "error": ... }`,
 * and a request that never reached the API (the usual case — it is not running) has no status at all.
 */
async function toError(response: Response): Promise<ApiError> {
  let message = `${response.status} ${response.statusText}`
  try {
    const body = await response.json()
    if (body && typeof body === 'object') {
      const detail = (body as Record<string, unknown>).error ?? (body as Record<string, unknown>).title
      if (typeof detail === 'string' && detail.length > 0) {
        message = detail
      }
    }
  } catch {
    // A non-JSON error body leaves the status line as the message.
  }
  return new ApiError(message, response.status)
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await authorizedFetch(`${BASE_URL}${path}`, {
      ...init,
      headers: { Accept: 'application/json', ...init?.headers },
    })
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause
    }
    throw new ApiError('Could not reach the API. Is SharePointAgent.Api running?', null)
  }

  if (!response.ok) {
    throw await toError(response)
  }
  return (await response.json()) as T
}

function query(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') {
      search.set(key, String(value))
    }
  }
  const text = search.toString()
  return text ? `?${text}` : ''
}

export function getSummary(signal?: AbortSignal): Promise<IndexStateSummary> {
  return request<IndexStateSummary>('/api/state/summary', { signal })
}

export function listIndexedFiles(
  options: IndexedFileQuery,
  signal?: AbortSignal,
): Promise<PagedResult<IndexedFileRow>> {
  return request<PagedResult<IndexedFileRow>>(
    `/api/state/indexed-files${query({ ...options })}`,
    { signal },
  )
}

export function listDeltaState(signal?: AbortSignal): Promise<DeltaStateRow[]> {
  return request<DeltaStateRow[]>('/api/state/delta', { signal })
}

export function resetDeltaState(driveId: string): Promise<{ reset: string }> {
  return request<{ reset: string }>(`/api/state/delta/${encodeURIComponent(driveId)}/reset`, {
    method: 'POST',
  })
}

export function deleteDeltaState(driveId: string): Promise<{ deleted: string }> {
  return request<{ deleted: string }>(`/api/state/delta/${encodeURIComponent(driveId)}`, {
    method: 'DELETE',
  })
}

export function listConversations(signal?: AbortSignal): Promise<ChatConversation[]> {
  return request<ChatConversation[]>('/api/chat/conversations', { signal })
}

export function createConversation(options?: {
  userId?: string | null
  agentId?: string | null
}): Promise<ChatConversation> {
  return request<ChatConversation>('/api/chat/conversations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      title: null,
      userId: options?.userId?.trim() || null,
      agentId: options?.agentId || null,
    }),
  })
}

async function downloadBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  let response: Response
  try {
    response = await authorizedFetch(`${BASE_URL}${path}`, { signal })
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause
    throw new ApiError('Could not reach the API. Is SharePointAgent.Api running?', null)
  }
  if (!response.ok) throw await toError(response)
  return response.blob()
}

export function downloadIndexedFile(file: Pick<IndexedFileRow, 'driveId' | 'itemId'>, signal?: AbortSignal): Promise<Blob> {
  return downloadBlob(
    `/api/state/indexed-files/${encodeURIComponent(file.driveId)}/${encodeURIComponent(file.itemId)}/content`,
    signal,
  )
}

export function getIndexedFileMarkdown(file: Pick<IndexedFileRow, 'driveId' | 'itemId'>, signal?: AbortSignal): Promise<{ markdown: string }> {
  return request<{ markdown: string }>(
    `/api/state/indexed-files/${encodeURIComponent(file.driveId)}/${encodeURIComponent(file.itemId)}/markdown`,
    { signal },
  )
}

export function reindexIndexedFile(file: Pick<IndexedFileRow, 'driveId' | 'itemId'>): Promise<IndexedFileRow> {
  return request<IndexedFileRow>(
    `/api/state/indexed-files/${encodeURIComponent(file.driveId)}/${encodeURIComponent(file.itemId)}/reindex`,
    { method: 'POST' },
  )
}

export function downloadAttachmentFile(id: string, signal?: AbortSignal): Promise<Blob> {
  return downloadBlob(`/api/attachment-files/${encodeURIComponent(id)}/download`, signal)
}

export function getAttachmentFileMarkdown(id: string, signal?: AbortSignal): Promise<{ markdown: string }> {
  return request<{ markdown: string }>(`/api/attachment-files/${encodeURIComponent(id)}/markdown`, { signal })
}

export function branchConversation(
  conversationId: string,
  messageId: string,
): Promise<ChatConversation> {
  return request<ChatConversation>(
    `/api/chat/conversations/${encodeURIComponent(conversationId)}/branch/${encodeURIComponent(messageId)}`,
    { method: 'POST' },
  )
}

export function deleteConversation(id: string): Promise<{ deleted: string }> {
  return request<{ deleted: string }>(`/api/chat/conversations/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
}

export function getThread(id: string, signal?: AbortSignal): Promise<ChatThread> {
  return request<ChatThread>(`/api/chat/conversations/${encodeURIComponent(id)}/messages`, { signal })
}

/** Runs one turn and reports text and tool progress as newline-delimited JSON arrives. */
export async function sendChatMessage(
  id: string,
  content: string,
  attachmentFileIds: string[],
  onEvent: (event: ChatStreamEvent) => void,
  signal?: AbortSignal,
): Promise<ChatTurnResult> {
  let response: Response
  try {
    response = await authorizedFetch(
      `${BASE_URL}/api/chat/conversations/${encodeURIComponent(id)}/messages`,
      {
        method: 'POST',
        headers: {
          Accept: 'application/x-ndjson',
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ content, attachmentFileIds }),
        signal,
      },
    )
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause
    throw new ApiError('Could not reach the API. Is SharePointAgent.Api running?', null)
  }

  if (!response.ok) throw await toError(response)
  if (!response.body) throw new ApiError('The API returned no response stream.', response.status)

  let question: ChatTurnResult['question'] | null = null
  let answer: ChatTurnResult['answer'] | null = null
  let title = ''
  let buffer = ''
  const decoder = new TextDecoder()

  const acceptLine = (line: string) => {
    if (!line.trim()) return
    const event = JSON.parse(line) as ChatStreamEvent
    onEvent(event)

    if (event.type === 'started') {
      question = event.question
      title = event.title
    } else if (event.type === 'completed') {
      answer = event.answer
      title = event.title
    } else if (event.type === 'error') {
      throw new ApiError(event.message, response.status)
    }
  }

  const reader = response.body.getReader()
  while (true) {
    const { value, done } = await reader.read()
    if (done) break

    buffer += decoder.decode(value, { stream: true })
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    for (const line of lines) acceptLine(line)
  }

  buffer += decoder.decode()
  acceptLine(buffer)

  if (!question || !answer) {
    throw new ApiError('The assistant stream ended before the turn completed.', response.status)
  }

  return { question, answer, title }
}

export async function uploadAttachmentFile(file: File, signal?: AbortSignal): Promise<AttachmentFileRecord> {
  const body = new FormData()
  body.append('file', file)
  return request<AttachmentFileRecord>('/api/attachment-files', { method: 'POST', body, signal })
}

export function getAttachmentOptions(signal?: AbortSignal): Promise<{ allowedFileExtensions: string[]; textFileExtensions: string[] }> {
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

export function listAgents(signal?: AbortSignal): Promise<AgentDefinition[]> {
  return request<AgentDefinition[]>('/api/agents', { signal })
}

export function getAgent(id: string, signal?: AbortSignal): Promise<AgentDefinition> {
  return request<AgentDefinition>(`/api/agents/${encodeURIComponent(id)}`, { signal })
}

export function getDefaultAgentInstructions(
  signal?: AbortSignal,
): Promise<{ instructions: string; modelId: string }> {
  return request<{ instructions: string; modelId: string }>('/api/agents/default-instructions', {
    signal,
  })
}

export function createAgent(
  name: string,
  modelId: string,
  instructions?: string,
): Promise<AgentDefinition> {
  return request<AgentDefinition>('/api/agents', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, modelId, instructions: instructions?.trim() || null }),
  })
}

export function updateAgent(
  id: string,
  name: string,
  modelId: string,
  instructions: string,
): Promise<AgentDefinition> {
  return request<AgentDefinition>(`/api/agents/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, modelId, instructions }),
  })
}

export function listFeedback(
  options: { feedback?: ChatFeedback; search?: string; skip?: number; top?: number },
  signal?: AbortSignal,
): Promise<FeedbackPage> {
  return request<FeedbackPage>(`/api/chat/feedback${query({ ...options })}`, { signal })
}

/** Records a reaction to one answer, or clears it with null. */
export function setMessageFeedback(
  messageId: string,
  feedback: ChatFeedback | null,
): Promise<{ id: string; feedback: ChatFeedback | null }> {
  return request(`/api/chat/messages/${encodeURIComponent(messageId)}/feedback`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ feedback }),
  })
}

export function listSubscriptions(signal?: AbortSignal): Promise<SubscriptionOverview> {
  return request<SubscriptionOverview>('/api/subscriptions', { signal })
}

/**
 * Omitting `days` uses the API's configured `SharePoint:SubscriptionLifetimeDays`, and omitting
 * `notificationUrl` uses its configured `SharePoint:NotificationUrl`.
 */
export function createSubscription(
  name: string,
  days?: number,
  notificationUrl?: string,
  clientState?: string,
): Promise<SubscriptionView> {
  return request<SubscriptionView>('/api/subscriptions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      name: name.trim(),
      days: days ?? null,
      notificationUrl: notificationUrl?.trim() || null,
      clientState: clientState?.trim() || null,
    }),
  })
}

export function renewSubscription(id: string, days?: number): Promise<SubscriptionView> {
  return request<SubscriptionView>(`/api/subscriptions/${encodeURIComponent(id)}/renew`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ days: days ?? null }),
  })
}

/**
 * Changes a subscription's settings. A new name, URL, or client state may require a replacement;
 * the result says whether the subscription ID changed.
 */
export function updateSubscription(
  id: string,
  name: string,
  days: number,
  notificationUrl: string,
  clientState?: string,
): Promise<UpdateSubscriptionResult> {
  return request<UpdateSubscriptionResult>(`/api/subscriptions/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: name.trim(), days, notificationUrl: notificationUrl.trim() || null, clientState: clientState?.trim() || null }),
  })
}

export function setSubscriptionAutoRenew(id: string, enabled: boolean): Promise<{ id: string; enabled: boolean }> {
  return request(`/api/subscriptions/${encodeURIComponent(id)}/auto-renew`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabled }),
  })
}

export function deleteSubscription(id: string): Promise<{ deleted: string }> {
  return request<{ deleted: string }>(`/api/subscriptions/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
}

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
