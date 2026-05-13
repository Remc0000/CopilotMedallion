import { IPublicClientApplication } from '@azure/msal-browser'

export async function getToken(pca: IPublicClientApplication, scope: string): Promise<string> {
  const accounts = pca.getAllAccounts()
  if (accounts.length === 0) throw new Error('no account')
  const res = await pca.acquireTokenSilent({ scopes: [scope], account: accounts[0] })
  return res.accessToken
}

export async function getFabricToken(pca: IPublicClientApplication, scope: string): Promise<string> {
  return getToken(pca, scope)
}

export async function getOnelakeToken(pca: IPublicClientApplication): Promise<string | null> {
  try { return await getToken(pca, 'https://storage.azure.com/.default') }
  catch { return null }
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
