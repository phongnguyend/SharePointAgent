import { request, downloadBlob } from './http'

export interface SignatureRequest {
  id: string
  provider: string
  subject: string
  message: string | null
  recipientsJson: string
  status: string
  externalId: string | null
  createdAtUtc: string
}

const signaturesPath = (id: string) => `/api/attachment-files/${encodeURIComponent(id)}/signatures`

export const getSigningProviders = (signal?: AbortSignal) => request<string[]>('/api/attachment-files/signing-options', { signal })

export interface AdobeAuthorizationTokens {
  accessToken: string
  refreshToken: string
  apiAccessPoint: string
  expiresIn: number | null
  tokenType: string | null
  webAccessPoint: string | null
  accessTokenUrl: string
  clientId: string
  clientSecret: string
  timestamp: number
  authUrl: string
  redirectUri: string
  requestedScope: string
  providerResponse: Record<string, unknown>
}

export const getAdobeAuthorizationConfiguration = (signal?: AbortSignal) => request<{
  configured: boolean
  redirectUri: string
  clientId: string
  clientSecretConfigured: boolean
  authUrl: string
  accessTokenUrl: string
}>(
  '/api/admin/document-signing/adobe', { signal, cache: 'no-store' })

export const beginAdobeAuthorization = () => request<{ state: string; url: string }>(
  '/api/admin/document-signing/adobe/authorize', { method: 'POST', cache: 'no-store' })

export const exchangeAdobeAuthorization = (input: { state: string; code: string; apiAccessPoint: string | null }, signal?: AbortSignal) => request<AdobeAuthorizationTokens>(
  '/api/admin/document-signing/adobe/exchange', { method: 'POST', cache: 'no-store', signal,
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) })

export const listSignatureRequests = (id: string, signal?: AbortSignal) => request<SignatureRequest[]>(signaturesPath(id), { signal })

export const deleteSignatureRequest = (id: string, requestId: string) => request<{ deleted: boolean }>(
  `${signaturesPath(id)}/${encodeURIComponent(requestId)}`, { method: 'DELETE' })

export const createSignatureRequest = (id: string, input: {
  provider: string; subject: string; message: string; recipients: { name: string; email: string }[]; clientRequestId: string
}) => request<SignatureRequest>(signaturesPath(id), {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input),
})

export const prepareSignatureRequest = (id: string, requestId: string) => request<{ url: string }>(
  `${signaturesPath(id)}/${encodeURIComponent(requestId)}/prepare`, { method: 'POST' })

export const refreshSignatureRequest = (id: string, requestId: string) => request<SignatureRequest>(
  `${signaturesPath(id)}/${encodeURIComponent(requestId)}/refresh`, { method: 'POST' })

export const downloadSignatureDocument = (id: string, requestId: string, audit: boolean, signal?: AbortSignal) => downloadBlob(
  `${signaturesPath(id)}/${encodeURIComponent(requestId)}/download?audit=${audit}`, signal)

export type SigningFieldType = 'signature' | 'initials' | 'date' | 'text'

/** Coordinates are fractions of the displayed page, measured from its top-left corner. */
export interface SigningField {
  id: string
  type: SigningFieldType
  page: number
  x: number
  y: number
  width: number
  height: number
  value: string | null
}

export const getSigningFields = (id: string, requestId: string, signal?: AbortSignal) => request<{ status: string; fields: SigningField[] }>(
  `${signaturesPath(id)}/${encodeURIComponent(requestId)}/fields`, { signal, cache: 'no-store' })

export const saveSigningFields = (id: string, requestId: string, fields: SigningField[]) => request<{ status: string; fields: SigningField[] }>(
  `${signaturesPath(id)}/${encodeURIComponent(requestId)}/fields`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ fields }),
  })

export const completeInAppSigning = (id: string, requestId: string, pdf: Blob) => request<SignatureRequest>(
  `${signaturesPath(id)}/${encodeURIComponent(requestId)}/complete`, {
    method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: pdf,
  })

export interface SigningTemplateSummary {
  id: string
  name: string
  fieldCount: number
  pageCount: number
  updatedAtUtc: string
}

/** A personal field layout. Values are never stored, so every field comes back empty. */
export interface SigningTemplate {
  id: string
  name: string
  pageCount: number
  fields: SigningField[]
  updatedAtUtc: string
}

const templatesPath = '/api/signing-templates'

export const listSigningTemplates = (signal?: AbortSignal) => request<SigningTemplateSummary[]>(templatesPath, { signal, cache: 'no-store' })

export const getSigningTemplate = (templateId: string) => request<SigningTemplate>(
  `${templatesPath}/${encodeURIComponent(templateId)}`, { cache: 'no-store' })

export const createSigningTemplate = (input: { name: string; fields: SigningField[]; pageCount: number }) => request<SigningTemplate>(templatesPath, {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input),
})

export const updateSigningTemplate = (templateId: string, input: { name: string; fields: SigningField[]; pageCount: number }) => request<SigningTemplate>(
  `${templatesPath}/${encodeURIComponent(templateId)}`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input),
  })

export const renameSigningTemplate = (templateId: string, name: string) => request<SigningTemplate>(
  `${templatesPath}/${encodeURIComponent(templateId)}`, {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }),
  })

export const deleteSigningTemplate = (templateId: string) => request<{ deleted: boolean }>(
  `${templatesPath}/${encodeURIComponent(templateId)}`, { method: 'DELETE' })
