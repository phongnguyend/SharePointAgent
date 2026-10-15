import { downloadBlob, query, request } from './http'

export interface AgentSkillScope { agentId?: string; subAgentId?: string }
export interface AgentSkill {
  id: string
  name: string
  description: string
  scope: string
  isDefault: boolean
}
export const agentSkillScopeKey = (scope: AgentSkillScope) => scope.subAgentId
  ? `sub:${scope.subAgentId.replaceAll('-', '')}` : scope.agentId ? `agent:${scope.agentId.replaceAll('-', '')}` : 'global'
const base = '/api/agents/skills'
export const downloadAgentSkill = (scope: AgentSkillScope, id: string) =>
  downloadBlob(`${base}/${encodeURIComponent(id)}/archive${query({ ...scope })}`)
export const downloadAgentSkillFile = (scope: AgentSkillScope, id: string, path: string, signal?: AbortSignal) =>
  downloadBlob(`${base}/${encodeURIComponent(id)}/download${query({ ...scope, path })}`, signal)
export const listAgentSkills = (scope: AgentSkillScope, signal?: AbortSignal) =>
  request<AgentSkill[]>(base + query({ ...scope }), { signal })
export function uploadAgentSkill(scope: AgentSkillScope, file: File) {
  const body = new FormData()
  body.append('file', file)
  return request<AgentSkill>(base + query({ ...scope }), { method: 'POST', body })
}
export function replaceAgentSkill(scope: AgentSkillScope, id: string, file: File) {
  const body = new FormData()
  body.append('file', file)
  return request<AgentSkill>(`${base}/${encodeURIComponent(id)}${query({ ...scope })}`, { method: 'PUT', body })
}
export const deleteAgentSkill = (scope: AgentSkillScope, id: string) =>
  request<void>(`${base}/${encodeURIComponent(id)}${query({ ...scope })}`, { method: 'DELETE' })
export const listAgentSkillFiles = (scope: AgentSkillScope, id: string, signal?: AbortSignal) =>
  request<{ path: string; size: number }[]>(`${base}/${encodeURIComponent(id)}/files${query({ ...scope })}`, { signal })
export const readAgentSkillFile = (scope: AgentSkillScope, id: string, path: string, signal?: AbortSignal) =>
  request<{ content: string | null; message: string | null }>(`${base}/${encodeURIComponent(id)}/file${query({ ...scope, path })}`, { signal })
