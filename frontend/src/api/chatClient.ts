import { authorizedFetch } from '../auth'
import { BASE_URL, ApiError, toError, request, query, downloadBlob } from './http'
import type {
  ChatConversation,
  ChatSandboxSession,
  ChatWorkspace,
  FileSystemListing,
  ChatFeedback,
  ChatStreamEvent,
  ChatThread,
  ChatTurnResult,
  FeedbackPage,
} from './types'

export type WorkspaceFileOperation = 'mkdir' | 'rename' | 'delete' | 'copy' | 'move' | 'upload'

export const manageWorkspaceFile = (conversationId: string, operation: WorkspaceFileOperation, path: string, destination?: string, content?: string) =>
  request<{ path: string }>(`/api/chat/conversations/${encodeURIComponent(conversationId)}/files/manage`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ operation, path, destination, content }),
  })

export async function uploadWorkspaceFile(conversationId: string, path: string, file: File) {
  if (file.size > 5 * 1024 * 1024) {
    throw new Error('Sandbox uploads are limited to 5 MB per file.')
  }
  const bytes = new Uint8Array(await file.arrayBuffer())
  let binary = ''
  for (let offset = 0; offset < bytes.length; offset += 32768) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 32768))
  }
  return manageWorkspaceFile(conversationId, 'upload', path, undefined, btoa(binary))
}

export function listConversations(signal?: AbortSignal): Promise<ChatConversation[]> {
  return request<ChatConversation[]>('/api/chat/conversations', { signal })
}

export function createConversation(options?: {
  userId?: string | null
  agentId?: string | null
  workspaceId?: string | null
}): Promise<ChatConversation> {
  return request<ChatConversation>('/api/chat/conversations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      title: null,
      userId: options?.userId?.trim() || null,
      agentId: options?.agentId || null,
      workspaceId: options?.workspaceId || null,
    }),
  })
}

/**
 * Lists the agent's working directory for this conversation. It runs no turn and spends no tokens,
 * in either execution mode.
 */
export function listConversationFiles(
  id: string,
  path: string | null,
  recursive: boolean,
  signal?: AbortSignal,
): Promise<FileSystemListing> {
  const query = new URLSearchParams()
  if (path) {
    query.set('path', path)
  }
  if (recursive) {
    query.set('recursive', 'true')
  }
  const suffix = query.toString()
  return request<FileSystemListing>(
    `/api/chat/conversations/${encodeURIComponent(id)}/files${suffix ? `?${suffix}` : ''}`,
    { signal },
  )
}

/**
 * One file out of the sandbox, as a blob. `download` asks for it as an attachment; without it the
 * API serves inline only the types that cannot carry script, and hands the rest over as a download
 * regardless. Runs no turn either way.
 */
export function downloadConversationFile(
  id: string,
  path: string,
  download: boolean,
  signal?: AbortSignal,
): Promise<Blob> {
  const query = new URLSearchParams({ path })
  if (download) {
    query.set('download', 'true')
  }
  return downloadBlob(`/api/chat/conversations/${encodeURIComponent(id)}/files/content?${query}`, signal)
}

/**
 * Discards the dynamic session or sandbox the API's own agent uses for this conversation, with its files.
 * A workspace's conversations share it, so all of them start afresh on their next turn.
 */
export function resetConversationEnvironment(id: string): Promise<{ reset: string }> {
  return request<{ reset: string }>(`/api/chat/conversations/${encodeURIComponent(id)}/session/reset`, {
    method: 'POST',
  })
}

/**
 * Chooses where the API's own agent keeps this conversation's files: on its workspace when it is in one, so
 * all of the workspace's conversations move together. Null returns to the configured default. The previous
 * environment is kept, so choosing it again finds its files.
 */
export function setConversationWorkspaceMode(id: string, mode: string | null): Promise<{ mode: string | null }> {
  return request<{ mode: string | null }>(`/api/chat/conversations/${encodeURIComponent(id)}/session/mode`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ mode }),
  })
}

/**
 * Chooses where this conversation's agent runs: in the API ('Local') or as the Foundry hosted agent. On its
 * workspace when it is in one. Null returns to the configured default. Each keeps its own files.
 */
export function setConversationAgentMode(id: string, mode: string | null): Promise<{ mode: string | null }> {
  return request<{ mode: string | null }>(`/api/chat/conversations/${encodeURIComponent(id)}/session/agent-mode`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ mode }),
  })
}

/** Reads the sandbox binding for inspection. It runs no turn and changes nothing. */
export function getConversationSession(id: string, signal?: AbortSignal): Promise<ChatSandboxSession> {
  return request<ChatSandboxSession>(`/api/chat/conversations/${encodeURIComponent(id)}/session`, { signal })
}

export function listWorkspaces(signal?: AbortSignal): Promise<ChatWorkspace[]> {
  return request<ChatWorkspace[]>('/api/chat/workspaces', { signal })
}

export function createWorkspace(name: string, instructions: string | null): Promise<ChatWorkspace> {
  return request<ChatWorkspace>('/api/chat/workspaces', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, instructions }),
  })
}

/** Replaces the name and the rules together. The rules apply from the next turn asked here. */
export function updateWorkspace(id: string, name: string, instructions: string | null): Promise<ChatWorkspace> {
  return request<ChatWorkspace>(`/api/chat/workspaces/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, instructions }),
  })
}

/** Removes the workspace. Its conversations stay, each back on a sandbox of its own. */
export function deleteWorkspace(id: string): Promise<{ deleted: string }> {
  return request<{ deleted: string }>(`/api/chat/workspaces/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
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

export interface TranscriptionOptions {
  enabled: boolean
  maxBytes: number
  maxSeconds: number
}

export const getTranscriptionOptions = (signal?: AbortSignal) =>
  request<TranscriptionOptions>('/api/chat/transcriptions/options', { signal })

/** Sends a composer recording for speech-to-text. The audio is not stored. */
export const transcribeAudio = (audio: Blob, durationSeconds: number | undefined, signal?: AbortSignal) => request<{ text: string }>(
  `/api/chat/transcriptions${query({ durationSeconds: durationSeconds === undefined ? undefined : Math.round(durationSeconds * 10) / 10 })}`, {
  method: 'POST', headers: { 'Content-Type': audio.type || 'audio/webm' }, body: audio, signal,
})

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
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause
    }
    throw new ApiError('Could not reach the API. Is SharePointAgent.Api running?', null)
  }

  if (!response.ok) {
    throw await toError(response)
  }
  if (!response.body) {
    throw new ApiError('The API returned no response stream.', response.status)
  }

  let question: ChatTurnResult['question'] | null = null
  let answer: ChatTurnResult['answer'] | null = null
  let title = ''
  let buffer = ''
  const decoder = new TextDecoder()

  const acceptLine = (line: string) => {
    if (!line.trim()) {
      return
    }
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
    if (done) {
      break
    }

    buffer += decoder.decode(value, { stream: true })
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    for (const line of lines) {
      acceptLine(line)
    }
  }

  buffer += decoder.decode()
  acceptLine(buffer)

  if (!question || !answer) {
    throw new ApiError('The assistant stream ended before the turn completed.', response.status)
  }

  return { question, answer, title }
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
