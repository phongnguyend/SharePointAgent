import { request } from './http'
import type {
  AgentDefinition,
} from './types'

export interface SubAgentInput {
  name: string
  description: string
  modelId: string
  instructions: string
  tools: string[]
  skills: string[]
}

export interface SubAgentDefinition extends SubAgentInput {
  id: string
  parentAgentId: string
  updatedAtUtc: string
}

const subAgentPath = (parentId: string) => `/api/agents/${encodeURIComponent(parentId)}/sub-agents`

export function listSubAgents(parentId: string, signal?: AbortSignal): Promise<SubAgentDefinition[]> {
  return request(subAgentPath(parentId), { signal })
}

export function saveSubAgent(parentId: string, id: string | null, body: SubAgentInput): Promise<SubAgentDefinition> {
  return request(subAgentPath(parentId) + (id ? `/${encodeURIComponent(id)}` : ''), {
    method: id ? 'PUT' : 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export function deleteSubAgent(parentId: string, id: string): Promise<void> {
  return request(`${subAgentPath(parentId)}/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

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
  tools: string[] | null = null,
  skills: string[] | null = null,
): Promise<AgentDefinition> {
  return request<AgentDefinition>('/api/agents', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, modelId, instructions: instructions?.trim() || null, tools, skills }),
  })
}

export function updateAgent(
  id: string,
  name: string,
  modelId: string,
  instructions: string,
  tools: string[] | null = null,
  skills: string[] | null = null,
): Promise<AgentDefinition> {
  return request<AgentDefinition>(`/api/agents/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, modelId, instructions, tools, skills }),
  })
}
