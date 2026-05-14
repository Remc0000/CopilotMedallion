import { useEffect, useState } from 'react'
import { useMsal, useIsAuthenticated } from '@azure/msal-react'
import {
  Button, Title1, Title3, Body1, Caption1, Dropdown, Option, Input, Label, Spinner,
  Card, CardHeader, makeStyles, tokens, MessageBar, MessageBarBody, MessageBarTitle,
  Checkbox, Link as FLink, Textarea
} from '@fluentui/react-components'
import { AppConfig, Lakehouse, Table, Run, SpecResponse } from './types'
import { api, getFabricToken, getOnelakeToken, inFabric, fabricWorkspaceId, fabricItemId } from './api'

const useStyles = makeStyles({
  shell: { maxWidth: '900px', margin: '0 auto', padding: '24px', display: 'flex', flexDirection: 'column', gap: '16px' },
  row: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' as const },
  status: { padding: '12px 16px', backgroundColor: tokens.colorNeutralBackground2, borderRadius: '4px' },
  tables: { maxHeight: '260px', overflow: 'auto', padding: '8px 12px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: '4px' }
})

export default function App({ appConfig }: { appConfig: AppConfig }) {
  const s = useStyles()
  const { instance } = useMsal()
  const signedIn = useIsAuthenticated()
  const [token, setToken] = useState<string | null>(null)
  const [onelakeToken, setOnelakeToken] = useState<string | null>(null)
  const [lakes, setLakes] = useState<Lakehouse[]>([])
  const [sourceId, setSourceId] = useState<string | null>(null)
  const [tables, setTables] = useState<Table[]>([])
  const [selectedTables, setSelectedTables] = useState<Set<string>>(new Set())
  const [targetName, setTargetName] = useState('')
  const [specDraft, setSpecDraft] = useState('')
  const [previewRunId, setPreviewRunId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [busyMsg, setBusyMsg] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [specs, setSpecs] = useState<SpecResponse | null>(null)
  const [run, setRun] = useState<Run | null>(null)

  const inFabricRuntime = inFabric  // imported from api.ts
  const [fabricSignedIn, setFabricSignedIn] = useState(false)
  const effectivelySignedIn = signedIn || fabricSignedIn

  async function signIn() {
    setError(null)
    try {
      await instance.loginPopup({ scopes: [appConfig.scope] })
    } catch (e: any) { setError(e.message ?? String(e)) }
  }

  async function signOut() {
    sessionStorage.clear()
    try { await instance.logoutPopup({ postLogoutRedirectUri: window.location.origin }) }
    catch { /* ignore */ }
  }

  // When loaded inside Fabric: wait for the workload host to postMessage a Fabric token,
  // then mark "signed in" and proceed. No MSAL popup is used inside the iframe.
  useEffect(() => {
    if (!inFabricRuntime || fabricSignedIn) return
    let cancelled = false
    ;(async () => {
      try {
        await getFabricToken(instance, appConfig.scope)
        if (!cancelled) setFabricSignedIn(true)
      } catch (e: any) {
        if (!cancelled) setError(`Fabric host did not provide a token: ${e?.message ?? e}`)
      }
    })()
    return () => { cancelled = true }
  }, [inFabricRuntime])

  const [sourceLakehouseName, setSourceLakehouseName] = useState<string | null>(null)
  const [sourceWorkspaceOverride, setSourceWorkspaceOverride] = useState<string | null>(null)
  const [targetLakehouseId, setTargetLakehouseId] = useState<string | null>(null)
  const [targetLakehouseName2, setTargetLakehouseName2] = useState<string | null>(null)
  const [targetWorkspaceId, setTargetWorkspaceId] = useState<string | null>(null)

  // Inside Fabric: receive picker results from the workload host.
  useEffect(() => {
    if (!inFabricRuntime) return
    function handler(ev: MessageEvent) {
      if (ev.data?.type === 'copilot-medallion-source-picked') {
        const sw = ev.data.workspaceId || null
        setSourceWorkspaceOverride(sw)
        ;(window as any).__copilotMedallionSourceWs = sw
        const lhId = ev.data.lakehouseId || null
        setSourceId(lhId)
        setSourceLakehouseName(ev.data.lakehouseName || null)
        const tabs: string[] = Array.isArray(ev.data.tables) ? ev.data.tables : []
        if (tabs.length === 0 && ev.data.onlyRoot && lhId) {
          // User picked the root "Tables" node — enumerate all tables in that lakehouse via backend.
          setBusy(true); setBusyMsg('Enumerating tables in source lakehouse...')
          ensureToken().then(({ fabric }) =>
            api<Table[]>(`/api/sources/lakehouses/${lhId}/tables`, fabric)
              .then(list => {
                const names = list.map(t => t.name)
                setTables(list)
                setSelectedTables(new Set(names))
              })
              .catch(e => setError(String(e)))
              .finally(() => { setBusy(false); setBusyMsg('') })
          )
        } else {
          setSelectedTables(new Set(tabs))
          setTables(tabs.map(t => ({ name: t })))
        }
      }
      if (ev.data?.type === 'copilot-medallion-target-picked') {
        const tw = ev.data.workspaceId || null
        ;(window as any).__copilotMedallionTargetWs = tw
        setTargetWorkspaceId(tw)
        setTargetLakehouseId(ev.data.lakehouseId || null)
        setTargetLakehouseName2(ev.data.lakehouseName || null)
        if (ev.data.lakehouseName) setTargetName(ev.data.lakehouseName)
      }
      if (ev.data?.type === 'copilot-medallion-source-pick-error') {
        setError(`Picker failed: ${ev.data.message}`)
      }
    }
    window.addEventListener('message', handler)
    return () => window.removeEventListener('message', handler)
  }, [inFabricRuntime])

  function pickSourceViaFabric() {
    window.parent?.postMessage({ type: 'copilot-medallion-pick-source' }, '*')
  }
  useEffect(() => {
    if (!effectivelySignedIn || !fabricItemId || run) return
    ;(async () => {
      try {
        const { fabric } = await ensureToken()
        const resp = await api<{ run: Run, specMarkdown: string | null }>(`/api/runs/by-item/${fabricItemId}`, fabric)
        if (resp?.run) {
          setRun(resp.run)
          if (resp.run.sourceLakehouseId) setSourceId(resp.run.sourceLakehouseId)
          if (resp.run.tablesCsv) setSelectedTables(new Set(resp.run.tablesCsv.split(',').filter(Boolean)))
          if (resp.run.targetLakehouseName) setTargetName(resp.run.targetLakehouseName)
          if (resp.specMarkdown) setSpecDraft(resp.specMarkdown)
          setPreviewRunId(resp.run.runId)
        }
      } catch (e: any) {
        // 404 means no run yet for this item — that's fine, normal first-time flow.
        const msg = String(e ?? '')
        if (!msg.includes('404')) console.warn('restore run failed', e)
      }
    })()
  }, [effectivelySignedIn, fabricItemId])

  async function ensureToken() {
    const t = await getFabricToken(instance, appConfig.scope)
    setToken(t)
    let olt: string | null = null
    try { olt = await getOnelakeToken(instance) } catch { /* optional */ }
    setOnelakeToken(olt)
    return { fabric: t, onelake: olt }
  }

  useEffect(() => {
    if (!effectivelySignedIn) return
    setBusy(true); setBusyMsg('Loading lakehouses...')
    ensureToken()
      .then(({ fabric }) => api<Lakehouse[]>('/api/sources/lakehouses', fabric))
      .then(setLakes)
      .catch(e => setError(String(e)))
      .finally(() => { setBusy(false); setBusyMsg('') })
  }, [effectivelySignedIn])

  async function grantOnelakeAccess() {
    setError(null); setBusy(true); setBusyMsg('Requesting OneLake consent...')
    try {
      const accounts = instance.getAllAccounts()
      const res = await instance.acquireTokenPopup({
        scopes: ['https://storage.azure.com/.default'],
        account: accounts[0]
      })
      setOnelakeToken(res.accessToken)
      // Retry tables if we have a sourceId
      if (sourceId && token) {
        const t = await api<Table[]>(`/api/sources/lakehouses/${sourceId}/tables`, token, undefined, res.accessToken)
        setTables(t)
      }
    } catch (e: any) { setError(String(e)) }
    finally { setBusy(false); setBusyMsg('') }
  }

  useEffect(() => {
    // Inside Fabric, tables come from the Fabric native picker (postMessage); skip backend fetch.
    if (inFabricRuntime) return
    if (!sourceId || !token) return
    setBusy(true); setBusyMsg('Loading tables...')
    setSelectedTables(new Set())
    ;(async () => {
      try {
        const t = await api<Table[]>(`/api/sources/lakehouses/${sourceId}/tables`, token, undefined, onelakeToken)
        setTables(t)
      } catch (e: any) {
        setError(String(e))
      } finally {
        setBusy(false); setBusyMsg('')
      }
    })()
  }, [sourceId, token, inFabricRuntime])

  useEffect(() => {
    if (!run) return
    if (['SpecsReady','Succeeded','Failed','Cancelled'].includes(run.status)) return
    const id = setInterval(async () => {
      try {
        const { fabric } = await ensureToken()
        const r = await api<Run>(`/api/runs/${run.runId}`, fabric)
        setRun(r)
      } catch (e) { /* ignore polling errors */ }
    }, 4000)
    return () => clearInterval(id)
  }, [run?.runId, run?.status])

  async function previewSpecs() {
    if (!sourceId || selectedTables.size === 0) return
    setError(null); setBusy(true); setBusyMsg('Generating spec template...')
    try {
      const { fabric } = await ensureToken()
      const resp = await api<{ markdown: string; runId: string; targetLakehouseName: string }>(
        '/api/specs/preview', fabric, {
          method: 'POST',
          body: JSON.stringify({
            sourceLakehouseId: sourceId,
            tables: Array.from(selectedTables),
            targetLakehouseName: targetName || null
          })
        })
      setSpecDraft(resp.markdown)
      setPreviewRunId(resp.runId)
      if (!targetName) setTargetName(resp.targetLakehouseName)
    } catch (e: any) { setError(String(e)) }
    finally { setBusy(false); setBusyMsg('') }
  }

  async function generateSpecs() {
    if (!sourceId || selectedTables.size === 0) return
    setError(null); setBusy(true); setBusyMsg(run ? 'Updating spec on GitHub...' : 'Pushing specs to GitHub...')
    try {
      const { fabric } = await ensureToken()
      const resp = await api<SpecResponse>('/api/specs', fabric, {
        method: 'POST',
        body: JSON.stringify({
          sourceLakehouseId: sourceId,
          tables: Array.from(selectedTables),
          targetLakehouseName: targetName || null,
          targetLakehouseId: targetLakehouseId || null,
          targetWorkspaceId: targetWorkspaceId || null,
          specMarkdown: specDraft || null,
          existingRunId: run?.runId ?? null,
        })
      })
      setSpecs(resp)
      const r = await api<Run>(`/api/runs/${resp.runId}`, fabric)
      setRun(r)
    } catch (e: any) { setError(String(e)) }
    finally { setBusy(false); setBusyMsg('') }
  }

  async function continueBuild() {
    if (!run) return
    setError(null); setBusy(true); setBusyMsg('Creating lakehouse + starting orchestrator notebook...')
    try {
      const { fabric } = await ensureToken()
      const r = await api<Run>('/api/build', fabric, {
        method: 'POST',
        body: JSON.stringify({ runId: run.runId })
      })
      setRun(r)
    } catch (e: any) { setError(String(e)) }
    finally { setBusy(false); setBusyMsg('') }
  }

  return (
    <div className={s.shell}>
      <Title1>🛠️ Copilot Medallion</Title1>
      <Body1>Automated Bronze/Silver/Gold + report build for Microsoft Fabric.</Body1>
      <Caption1>
        Workspace: <code>{fabricWorkspaceId || appConfig.workspaceId}</code> · Specs repo: <code>{appConfig.runsRepo}</code>
        {signedIn && <> · <FLink onClick={signOut} as="button" style={{ background: 'none', border: 'none', cursor: 'pointer' }}>Sign out</FLink></>}
      </Caption1>

      {!effectivelySignedIn && !inFabricRuntime && (
        <Card>
          <CardHeader header={<Title3>Sign in</Title3>} description={<Body1>Use your Entra account that has access to the Fabric workspace.</Body1>} />
          <Button appearance="primary" onClick={signIn}>Sign in with Microsoft</Button>
        </Card>
      )}

      {!effectivelySignedIn && inFabricRuntime && (
        <Spinner label="Connecting to Fabric..." />
      )}

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>Error</MessageBarTitle> {error}
            {error.includes('X-Onelake-Token') || error.includes('OneLake') || error.includes('storage.azure.com') ? (
              <div style={{ marginTop: 8 }}>
                <Button appearance="primary" onClick={grantOnelakeAccess}>Grant OneLake access</Button>
              </div>
            ) : null}
          </MessageBarBody>
        </MessageBar>
      )}

      {effectivelySignedIn && !onelakeToken && !inFabricRuntime && (
        <MessageBar intent="info">
          <MessageBarBody>
            <MessageBarTitle>OneLake access</MessageBarTitle>
            Schema-enabled lakehouses need a separate OneLake consent.
            <div style={{ marginTop: 8 }}>
              <Button onClick={grantOnelakeAccess}>Grant OneLake access</Button>
            </div>
          </MessageBarBody>
        </MessageBar>
      )}

      {effectivelySignedIn && (
        <Card>
          <CardHeader header={<Title3>1. Pick source &amp; tables</Title3>} />
          {inFabricRuntime ? (
            sourceId ? (
              <div className={s.row}>
                <Caption1>
                  Source: <code>{sourceLakehouseName ?? sourceId}</code> · {selectedTables.size} table{selectedTables.size === 1 ? '' : 's'}
                </Caption1>
              </div>
            ) : (
              <Body1>Use <b>Pick source Lakehouse & tables…</b> at the top to choose what to ingest.</Body1>
            )
          ) : (
            <>
              <div className={s.row}>
                <Label htmlFor="lh">Source Lakehouse</Label>
                <Dropdown id="lh" placeholder="Select..." onOptionSelect={(_, d) => setSourceId(d.optionValue ?? null)} value={lakes.find(l => l.id === sourceId)?.displayName ?? ''}>
                  {lakes.map(l => <Option key={l.id} value={l.id} text={l.displayName}>{l.displayName}</Option>)}
                </Dropdown>
              </div>
              {tables.length > 0 && (
                <>
                  <Label>Tables to load to Bronze ({selectedTables.size} selected)</Label>
                  <div className={s.tables}>
                    {tables.map(t => (
                      <div key={t.name}>
                        <Checkbox
                          checked={selectedTables.has(t.name)}
                          onChange={(_, d) => {
                            const next = new Set(selectedTables)
                            if (d.checked) next.add(t.name); else next.delete(t.name)
                            setSelectedTables(next)
                          }}
                          label={t.name}
                        />
                      </div>
                    ))}
                  </div>
                </>
              )}
            </>
          )}
          {(sourceId && selectedTables.size > 0) && (
            <>
              <div className={s.row}>
                <Label htmlFor="tn">Target Lakehouse name (optional)</Label>
                <Input id="tn" value={targetName} onChange={(_, d) => setTargetName(d.value)} placeholder="(auto-generated)" disabled={!!run} />
              </div>
              {!specDraft ? (
                <div className={s.row}>
                  <Button appearance="primary" disabled={busy || selectedTables.size === 0} onClick={previewSpecs}>
                    Preview spec
                  </Button>
                  {busy && <Spinner size="tiny" label={busyMsg} />}
                </div>
              ) : (
                <>
                  <Label htmlFor="spec">
                    Spec (editable)
                    {previewRunId && <Caption1>· run {previewRunId}</Caption1>}
                    {run && <Caption1>· current run {run.runId}</Caption1>}
                  </Label>
                  <Textarea
                    id="spec"
                    value={specDraft}
                    onChange={(_, d) => setSpecDraft(d.value)}
                    rows={20}
                    resize="vertical"
                    style={{ fontFamily: 'monospace', fontSize: 12, width: '100%' }}
                  />
                  <div className={s.row}>
                    <Button appearance="primary" disabled={busy} onClick={generateSpecs}>
                      {run ? 'Update spec on GitHub' : 'Push to GitHub'}
                    </Button>
                    {!run && (
                      <Button disabled={busy} onClick={() => { setSpecDraft(''); setPreviewRunId(null) }}>
                        Regenerate from template
                      </Button>
                    )}
                    {busy && <Spinner size="tiny" label={busyMsg} />}
                  </div>
                </>
              )}
            </>
          )}
        </Card>
      )}

      {run && (
        <Card>
          <CardHeader header={<Title3>2. Review &amp; build</Title3>} description={<Body1>Status: <strong>{run.status}</strong></Body1>} />
          <div className={s.status}>
            <div>Run ID: <code>{run.runId}</code></div>
            <div>Target Lakehouse: <code>{run.targetLakehouseName}</code></div>
            {run.specUrl && run.specUrl.startsWith('http') && (
              <div>Spec: <FLink href={run.specUrl} target="_blank">{run.specUrl}</FLink></div>
            )}
            {run.message && <div>Message: <code>{run.message}</code></div>}
            {run.targetLakehouseId && <div>Created Lakehouse: <code>{run.targetLakehouseId}</code></div>}
            {run.jobInstanceId && <div>Notebook Job: <code>{run.jobInstanceId}</code></div>}
          </div>
          {['SpecsReady','Succeeded','Failed','Cancelled'].includes(run.status) && (
            <div className={s.row}>
              <Button appearance="primary" disabled={busy} onClick={continueBuild}>
                {run.targetLakehouseId ? 'Re-build with updated spec' : 'Continue — build it'}
              </Button>
              {busy && <Spinner size="tiny" label={busyMsg} />}
            </div>
          )}
          {run.status === 'Succeeded' && (
            <MessageBar intent="success">
              <MessageBarBody><MessageBarTitle>Done</MessageBarTitle> Medallion built. Edit the spec above and press <b>Re-build with updated spec</b> to iterate — same lakehouse + notebook get updated in place.</MessageBarBody>
            </MessageBar>
          )}
          {(run.status === 'Failed' || run.status === 'Cancelled') && (
            <MessageBar intent="error">
              <MessageBarBody><MessageBarTitle>{run.status}</MessageBarTitle> {run.message ?? ''}</MessageBarBody>
            </MessageBar>
          )}
          {['Queued','GeneratingNotebook','CreatingLakehouse','ReusingLakehouse','DeployingNotebook','UpdatingNotebook','RunningNotebook','Building'].includes(run.status) && (
            <Spinner label={`Building... (${run.status})`} />
          )}
        </Card>
      )}
    </div>
  )
}
