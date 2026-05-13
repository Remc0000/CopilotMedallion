import { IPublicClientApplication } from '@azure/msal-browser'

export async function getToken(pca: IPublicClientApplication, scope: string, forceRefresh = false): Promise<string> {
  const accounts = pca.getAllAccounts()
  if (accounts.length === 0) throw new Error('no account')
  const res = await pca.acquireTokenSilent({ scopes: [scope], account: accounts[0], forceRefresh })
  return res.accessToken
}

export async function getFabricToken(pca: IPublicClientApplication, scope: string): Promise<string> {
  // Force refresh once per session so newly-granted scopes (e.g. Item.Execute.All) are picked up.
  const key = 'fabricTokenRefreshed:v2'
  const force = !sessionStorage.getItem(key)
  const tok = await getToken(pca, scope, force)
  if (force) sessionStorage.setItem(key, '1')
  return tok
}

export async function getOnelakeToken(pca: IPublicClientApplication): Promise<string | null> {
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
  const res = await fetch(path, { ...init, headers })
  if (!res.ok) {
    const t = await res.text()
    throw new Error(`${res.status} ${res.statusText}: ${t}`)
  }
  return res.json()
}
