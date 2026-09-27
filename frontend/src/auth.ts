import { PublicClientApplication, InteractionRequiredAuthError, BrowserCacheLocation } from '@azure/msal-browser'

export const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '')
let client: PublicClientApplication
let scope: string
let initialization: Promise<void> | undefined

export function initializeAuth(): Promise<void> {
  return initialization ??= (async () => {
    const response = await fetch(`${API_BASE}/api/auth/config`, { cache: 'no-store' })
    if (!response.ok) throw new Error('Could not load sign-in settings. Check that the API is running.')
    const config = await response.json() as { tenantId: string; clientId: string; scope: string }
    scope = config.scope
    client = new PublicClientApplication({
      auth: {
        clientId: config.clientId,
        authority: `https://login.microsoftonline.com/${config.tenantId}`,
        redirectUri: `${window.location.origin}/auth-redirect.html`,
        postLogoutRedirectUri: window.location.origin,
      },
      cache: { cacheLocation: BrowserCacheLocation.SessionStorage },
    })
    await client.initialize()
    const result = await client.handleRedirectPromise()
    if (result?.account) client.setActiveAccount(result.account)
    else if (!client.getActiveAccount() && client.getAllAccounts().length === 1) {
      client.setActiveAccount(client.getAllAccounts()[0])
    }
  })()
}

export function signedInAccount() { return client?.getActiveAccount() ?? null }
export async function signIn() {
  await initializeAuth()
  await client.loginRedirect({ scopes: [scope], prompt: 'select_account' })
}
export async function signOut() {
  await client.logoutRedirect({ account: signedInAccount() })
}

export async function authorizedFetch(url: string, init?: RequestInit): Promise<Response> {
  await initializeAuth()
  const account = signedInAccount()
  if (!account) throw new Error('Please sign in to continue.')
  let token: string
  try {
    token = (await client.acquireTokenSilent({ scopes: [scope], account })).accessToken
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      window.dispatchEvent(new Event('auth-required'))
      throw new Error('Your session needs attention. Please sign in again.')
    }
    throw error
  }
  const headers = new Headers(init?.headers)
  headers.set('Authorization', `Bearer ${token}`)
  const response = await fetch(url, { ...init, headers })
  if (response.status === 401) window.dispatchEvent(new Event('auth-required'))
  return response
}
