import { downloadBlob, query, request } from './http'

export type WorkspacePlaygroundProvider = 'dynamic-sessions' | 'sandboxes'
export type PlaygroundInstance = { id: string; state: string; canAccessFiles: boolean; createdAt: string | null; expiresAt: string | null }
export type PlaygroundListing = { configured: boolean; message: string | null; instances: PlaygroundInstance[]; truncated: boolean }
export type PlaygroundFile = { path: string; isDirectory: boolean; sizeBytes: number | null; modifiedUtc: string }
export type PlaygroundFiles = { path: string; entries: PlaygroundFile[]; truncated: boolean }
export type PlaygroundExecution = { language: string; code: string | null; scriptPath: string | null; arguments: string[]; workingDirectory: string; timeoutSeconds: number }
export type PlaygroundExecutionResult = { exitCode: number | null; timedOut: boolean; stdout: string; stderr: string }

const base = (provider: WorkspacePlaygroundProvider) => `/api/admin/workspace-playground/${provider}/instances`
const instance = (provider: WorkspacePlaygroundProvider, id: string) => `${base(provider)}/${encodeURIComponent(id)}`
const json = (body: unknown, signal?: AbortSignal, method = 'POST'): RequestInit => ({ method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal, cache: 'no-store' })

export const listPlaygroundInstances = (provider: WorkspacePlaygroundProvider, signal?: AbortSignal) =>
  request<PlaygroundListing>(base(provider), { signal, cache: 'no-store' })
export const createPlaygroundInstance = (provider: WorkspacePlaygroundProvider, signal?: AbortSignal) =>
  request<PlaygroundInstance>(base(provider), { method: 'POST', signal })
export const changePlaygroundInstance = (provider: WorkspacePlaygroundProvider, id: string, action: string, signal?: AbortSignal) =>
  request<void>(`${instance(provider, id)}/actions/${action}`, { method: 'POST', signal })
export const listPlaygroundFiles = (provider: WorkspacePlaygroundProvider, id: string, path: string, signal?: AbortSignal) =>
  request<PlaygroundFiles>(`${instance(provider, id)}/files${query({ path })}`, { signal, cache: 'no-store' })
export const downloadPlaygroundFile = (provider: WorkspacePlaygroundProvider, id: string, path: string, signal?: AbortSignal) =>
  downloadBlob(`${instance(provider, id)}/files/content${query({ path })}`, signal)
export const managePlaygroundFile = (provider: WorkspacePlaygroundProvider, id: string,
  change: { operation: string; path: string; destination?: string; content?: string }, signal?: AbortSignal) =>
  request(`${instance(provider, id)}/files`, json(change, signal))
export const writePlaygroundText = (provider: WorkspacePlaygroundProvider, id: string, path: string, content: string, overwrite: boolean, signal?: AbortSignal) =>
  request(`${instance(provider, id)}/files/text`, json({ path, content, overwrite }, signal, 'PUT'))
export const archivePlaygroundFiles = (provider: WorkspacePlaygroundProvider, id: string, operation: 'zip' | 'unzip', paths: string[], destination: string, signal?: AbortSignal) =>
  request(`${instance(provider, id)}/files/archive`, json({ operation, paths, destination }, signal))
export const executePlaygroundScript = (provider: WorkspacePlaygroundProvider, id: string, execution: PlaygroundExecution, signal?: AbortSignal) =>
  request<PlaygroundExecutionResult>(`${instance(provider, id)}/executions`, json(execution, signal))
