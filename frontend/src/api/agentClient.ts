import { request } from './http'
import type {
  AgentDefinition,
} from './types'

export function listAgents(signal?: AbortSignal): Promise<AgentDefinition[]> {
  return request<AgentDefinition[]>('/api/agents', { signal })
}

export function getAgentCapabilities(signal?: AbortSignal): Promise<import('./types').AgentCapabilities> {
  return request('/api/agents/capabilities', { signal })
}

export function getAgent(id: string, signal?: AbortSignal): Promise<AgentDefinition> {
  return request<AgentDefinition>(`/api/agents/${encodeURIComponent(id)}`, { signal })
}

export function getDefaultAgentInstructions(
  signal?: AbortSignal,
): Promise<{ instructions: string; modelId: string; mode: 'Local' | 'Foundry'; endpoint: string | null }> {
  return request<{ instructions: string; modelId: string; mode: 'Local' | 'Foundry'; endpoint: string | null }>('/api/agents/default-instructions', {
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
