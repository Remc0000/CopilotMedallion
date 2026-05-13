import React from 'react'
import ReactDOM from 'react-dom/client'
import { FluentProvider, webLightTheme } from '@fluentui/react-components'
import { PublicClientApplication } from '@azure/msal-browser'
import { MsalProvider } from '@azure/msal-react'
import App from './App'

async function bootstrap() {
  const cfgResp = await fetch('/api/config')
  const cfg = await cfgResp.json()
  const msalConfig = {
    auth: {
      clientId: cfg.clientId,
      authority: `https://login.microsoftonline.com/${cfg.tenantId || 'organizations'}`,
      redirectUri: window.location.origin,
    },
    cache: { cacheLocation: 'sessionStorage' }
  }
  const pca = new PublicClientApplication(msalConfig)
  await pca.initialize()
  await pca.handleRedirectPromise()

  ReactDOM.createRoot(document.getElementById('root')!).render(
    <React.StrictMode>
      <MsalProvider instance={pca}>
        <FluentProvider theme={webLightTheme}>
          <App appConfig={cfg} />
        </FluentProvider>
      </MsalProvider>
    </React.StrictMode>
  )
}

bootstrap().catch(err => {
  document.getElementById('root')!.innerHTML = `<pre style="color:#c00;padding:1rem">${err}</pre>`
})
