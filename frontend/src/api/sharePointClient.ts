import { request, query, downloadBlob } from './http'
import type {
  DeltaStateRow,
  IndexStateSummary,
  IndexedFileQuery,
  IndexedFileRow,
  PagedResult,
  SubscriptionOverview,
  SubscriptionView,
  UpdateSubscriptionResult,
} from './types'

export interface BrowseItem {
  id: string
  name: string
  isFolder: boolean
  size: number | null
  lastModifiedUtc: string | null
  webUrl: string | null
  parentId: string | null
  eTag: string | null
}

export interface BrowseListing {
  folder: BrowseItem
  breadcrumbs: BrowseItem[]
  items: BrowseItem[]
}

export interface RecycleBinItem {
  id: string
  name: string
  size: number | null
  deletedDateTime: string | null
  deletedFromLocation: string | null
}

export const browseSharePoint = (folderId: string, signal?: AbortSignal) =>
  request<BrowseListing>(`/api/browse/${query({ folderId })}`, { signal })

export const browseRecycleBin = (signal?: AbortSignal) =>
  request<{ siteName: string; recycleBinUrl: string | null; items: RecycleBinItem[] }>('/api/browse/recycle-bin', { signal })

export const createSharePointFolder = (parentId: string, name: string) =>
  request<BrowseItem>('/api/browse/folders', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ parentId, name }),
  })

export const changeSharePointItem = (item: BrowseItem, operation: 'rename' | 'delete' | 'copy' | 'move', name: string, destinationId?: string) =>
  request<{ completed: boolean }>(`/api/browse/${encodeURIComponent(item.id)}${operation === 'delete' ? query({ etag: item.eTag }) : operation === 'rename' ? '' : `/${operation}`}`, {
    method: operation === 'delete' ? 'DELETE' : operation === 'rename' ? 'PATCH' : 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: operation === 'delete' ? undefined : JSON.stringify({ name, destinationId, eTag: item.eTag }),
  })

export const uploadSharePointFile = (parentId: string, file: File) =>
  request<BrowseItem>(`/api/browse/upload${query({ parentId, name: file.name })}`, {
    method: 'PUT', headers: { 'Content-Type': 'application/octet-stream' }, body: file,
  })

export function getSensitivityLabels(signal?: AbortSignal): Promise<Record<string, string>> {
  return request<Record<string, string>>('/api/sensitivity-labels', { signal })
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
