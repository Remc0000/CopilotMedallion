import { IPublicClientApplication } from '@azure/msal-browser'

let fabricTokenFromHost: string | null = null
let storageTokenFromHost: string | null = null

export const inFabric = new URLSearchParams(window.location.search).get('inFabric') === '1'
export const fabricWorkspaceId = new URLSearchParams(window.location.search).get('workspaceId')
export const fabricItemId = new URLSearchParams(window.location.search).get('itemId')

// When running inside a Fabric workload iframe, the outer page sends tokens via postMessage.
// We send a "ready" once on load and on each request, since the outer side keeps the latest.
if (inFabric) {
  window.addEventListener('message', (ev) => {
    if (ev.data?.type === 'copilot-medallion-tokens') {
      if (ev.data.fabricToken) fabricTokenFromHost = ev.data.fabricToken
      if (ev.data.storageToken) storageTokenFromHost = ev.data.storageToken
      if (ev.data.onelakeToken) storageTokenFromHost = ev.data.onelakeToken
    }
  })
  window.parent?.postMessage({ type: 'copilot-medallion-ready' }, '*')
}

function waitForFabricToken(timeoutMs = 15000): Promise<string> {
  return new Promise((resolve, reject) => {
    if (fabricTokenFromHost) return resolve(fabricTokenFromHost)
    const start = Date.now()
    const t = setInterval(() => {
      if (fabricTokenFromHost) { clearInterval(t); resolve(fabricTokenFromHost) }
      else if (Date.now() - start > timeoutMs) { clearInterval(t); reject(new Error('Fabric host did not provide a token in time')) }
    }, 200)
    // nudge the host again
    window.parent?.postMessage({ type: 'copilot-medallion-ready' }, '*')
  })
}

export async function getToken(pca: IPublicClientApplication, scope: string, forceRefresh = false): Promise<string> {
  const accounts = pca.getAllAccounts()
  if (accounts.length === 0) throw new Error('no account')
  const res = await pca.acquireTokenSilent({ scopes: [scope], account: accounts[0], forceRefresh })
  return res.accessToken
}

export async function getFabricToken(pca: IPublicClientApplication, scope: string): Promise<string> {
  if (inFabric) return waitForFabricToken()
  const key = 'fabricTokenRefreshed:v2'
  const force = !sessionStorage.getItem(key)
  const tok = await getToken(pca, scope, force)
  if (force) sessionStorage.setItem(key, '1')
  return tok
}

export async function getOnelakeToken(pca: IPublicClientApplication): Promise<string | null> {
  if (inFabric) return storageTokenFromHost
  const accounts = pca.getAllAccounts()
  if (accounts.length === 0) return null
  try {
    const res = await pca.acquireTokenSilent({ scopes: ['https://storage.azure.com/.default'], account: accounts[0] })
    return res.accessToken
  } catch {
    try {
      const res = await pca.acquireTokenPopup({ scopes: ['https://storage.azure.com/.default'], account: accounts[0] })
      return res.accessToken
    } catch { return null }
  }
}

export async function api<T>(path: string, fabricToken: string, init?: RequestInit, onelakeToken?: string | null): Promise<T> {
  const headers: Record<string,string> = {
    ...(init?.headers as Record<string,string> || {}),
    'Content-Type': 'application/json',
    'X-Fabric-Token': fabricToken,
  }
  if (onelakeToken) headers['X-Onelake-Token'] = onelakeToken
  if (fabricWorkspaceId) headers['X-Fabric-Workspace-Id'] = fabricWorkspaceId
  if (fabricItemId) headers['X-Fabric-Item-Id'] = fabricItemId
  // The optional sourceWorkspaceId (Fabric picker) overrides the item workspace for source lookups.
  const sws = (typeof window !== 'undefined' && (window as any).__copilotMedallionSourceWs) as string | undefined
  if (sws) headers['X-Fabric-Source-Workspace-Id'] = sws
  const tws = (typeof window !== 'undefined' && (window as any).__copilotMedallionTargetWs) as string | undefined
  if (tws) headers['X-Fabric-Target-Workspace-Id'] = tws
  const res = await fetch(path, { ...init, headers })
  if (!res.ok) {
    const t = await res.text()
    throw new Error(`${res.status} ${res.statusText}: ${t}`)
  }
  return res.json()
}
