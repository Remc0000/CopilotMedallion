import { useEffect, useState } from 'react'
import { useMsal, useIsAuthenticated } from '@azure/msal-react'
import {
  Button, Title1, Title3, Body1, Caption1, Dropdown, Option, Input, Label, Spinner,
  Card, CardHeader, makeStyles, tokens, MessageBar, MessageBarBody, MessageBarTitle,
  Checkbox, Link as FLink, Textarea
} from '@fluentui/react-components'
import { AppConfig, Lakehouse, Table, Run, SpecResponse } from './types'
import { api, getFabricToken, getOnelakeToken } from './api'

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

  async function signIn() {
    setError(null)
    try {
      await instance.loginPopup({ scopes: [appConfig.scope] })
    } catch (e: any) { setError(e.message ?? String(e)) }
  }

  async function ensureToken() {
    const t = await getFabricToken(instance, appConfig.scope)
    setToken(t)
    // OneLake token is acquired lazily/best-effort; one resource per request.
    let olt: string | null = null
    try {
      olt = await getOnelakeToken(instance)
    } catch { /* will be requested on demand */ }
    setOnelakeToken(olt)
    return { fabric: t, onelake: olt }
  }

  useEffect(() => {
    if (!signedIn) return
    setBusy(true); setBusyMsg('Loading lakehouses...')
    ensureToken()
      .then(({ fabric }) => api<Lakehouse[]>('/api/sources/lakehouses', fabric))
      .then(setLakes)
      .catch(e => setError(String(e)))
      .finally(() => { setBusy(false); setBusyMsg('') })
  }, [signedIn])

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
  }, [sourceId, token])

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
    setError(null); setBusy(true); setBusyMsg('Pushing specs to GitHub...')
    try {
      const { fabric } = await ensureToken()
      const resp = await api<SpecResponse>('/api/specs', fabric, {
        method: 'POST',
        body: JSON.stringify({
          sourceLakehouseId: sourceId,
          tables: Array.from(selectedTables),
          targetLakehouseName: targetName || null,
          specMarkdown: specDraft || null
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
      <Caption1>Workspace: <code>{appConfig.workspaceId}</code> · Specs repo: <code>{appConfig.runsRepo}</code></Caption1>

      {!signedIn && (
        <Card>
          <CardHeader header={<Title3>Sign in</Title3>} description={<Body1>Use your Entra account that has access to the Fabric workspace.</Body1>} />
          <Button appearance="primary" onClick={signIn}>Sign in with Microsoft</Button>
        </Card>
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

      {signedIn && !onelakeToken && (
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

      {signedIn && !run && (
        <Card>
          <CardHeader header={<Title3>1. Pick source &amp; tables</Title3>} />
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
              <div className={s.row}>
                <Label htmlFor="tn">Target Lakehouse name (optional)</Label>
                <Input id="tn" value={targetName} onChange={(_, d) => setTargetName(d.value)} placeholder="(auto-generated)" />
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
                  <Label htmlFor="spec">Spec (editable) {previewRunId && <Caption1>· run {previewRunId}</Caption1>}</Label>
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
                      Push to GitHub
                    </Button>
                    <Button disabled={busy} onClick={() => { setSpecDraft(''); setPreviewRunId(null) }}>
                      Regenerate from template
                    </Button>
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
          <CardHeader header={<Title3>2. Review specs</Title3>} description={<Body1>Status: <strong>{run.status}</strong></Body1>} />
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
          {run.status === 'SpecsReady' && (
            <div className={s.row}>
              <Button appearance="primary" disabled={busy} onClick={continueBuild}>Continue — build it</Button>
              {busy && <Spinner size="tiny" label={busyMsg} />}
            </div>
          )}
          {run.status === 'Succeeded' && (
            <MessageBar intent="success">
              <MessageBarBody><MessageBarTitle>Done</MessageBarTitle> Medallion built. Check the lakehouse in Fabric.</MessageBarBody>
            </MessageBar>
          )}
          {(run.status === 'Failed' || run.status === 'Cancelled') && (
            <MessageBar intent="error">
              <MessageBarBody><MessageBarTitle>{run.status}</MessageBarTitle> {run.message ?? ''}</MessageBarBody>
            </MessageBar>
          )}
          {['CreatingLakehouse','DeployingNotebook','RunningNotebook','Building'].includes(run.status) && (
            <Spinner label={`Building... (${run.status})`} />
          )}
        </Card>
      )}
    </div>
  )
}
