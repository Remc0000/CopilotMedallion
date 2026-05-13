import { IPublicClientApplication } from '@azure/msal-browser'

export async function getFabricToken(pca: IPublicClientApplication, scope: string): Promise<string> {
  const accounts = pca.getAllAccounts()
  if (accounts.length === 0) throw new Error('no account')
  const res = await pca.acquireTokenSilent({ scopes: [scope], account: accounts[0] })
  return res.accessToken
}

export async function api<T>(path: string, token: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    ...init,
    headers: {
      ...(init?.headers || {}),
      'Content-Type': 'application/json',
      'X-Fabric-Token': token,
    }
  })
  if (!res.ok) {
    const t = await res.text()
    throw new Error(`${res.status} ${res.statusText}: ${t}`)
  }
  return res.json()
}
