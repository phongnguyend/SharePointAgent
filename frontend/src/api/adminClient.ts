import { request, query } from './http'
import type {
  AppUser,
  SystemAttachmentStorage,
  AppUserInput,
  AppUserStorageInput,
  PagedResult,
} from './types'

export interface ServiceHealthStatus {
  name: string
  status: 'healthy' | 'unhealthy' | 'not-configured' | 'misconfigured' | 'degraded' | 'unknown'
  message: string
  responseTimeMs: number | null
  checkedAtUtc: string
  lastHeartbeatUtc?: string | null
  lastSyncSucceededUtc?: string | null
  lastFailureUtc?: string | null
  subscriptionExpiresUtc?: string | null
}

export const getServiceHealth = (signal?: AbortSignal) =>
  request<ServiceHealthStatus[]>('/api/admin/service-health', { signal, cache: 'no-store' })

export interface SitePermissionInput {
  tenantId: string
  clientId: string
  clientSecret: string
  siteUrl: string
  targetClientId: string
  targetDisplayName: string
  role: 'read' | 'write'
}

export const getSitePermissionDefaults = (signal?: AbortSignal) =>
  request<Pick<SitePermissionInput, 'tenantId' | 'siteUrl' | 'targetClientId'>>('/api/admin/site-permissions/defaults', { signal, cache: 'no-store' })

export interface SitePermissionResult {
  siteId: string
  targetClientId: string
  role: string
  updated: boolean
}

export interface SitePermissionListing {
  siteId: string
  permissions: { permissionId: string; clientId: string; displayName: string; roles: string[] }[]
}

export const deleteSitePermission = (input: Pick<SitePermissionInput, 'tenantId' | 'clientId' | 'clientSecret' | 'siteUrl' | 'targetClientId'> & { permissionId: string }) =>
  request<{ deleted: string }>('/api/admin/site-permissions/delete', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input), cache: 'no-store',
  })

export const listSitePermissions = (input: Pick<SitePermissionInput, 'tenantId' | 'clientId' | 'clientSecret' | 'siteUrl'>) =>
  request<SitePermissionListing>('/api/admin/site-permissions/list', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input), cache: 'no-store',
  })

export const saveSitePermission = (input: SitePermissionInput) =>
  request<SitePermissionResult>('/api/admin/site-permissions', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input), cache: 'no-store',
  })

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
