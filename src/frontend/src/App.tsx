import { useEffect, useRef, useState } from 'react'
import { useMsal, useIsAuthenticated } from '@azure/msal-react'
import {
  Button, Title1, Title3, Body1, Caption1, Dropdown, Option, Input, Label, Spinner,
  Card, CardHeader, makeStyles, tokens, MessageBar, MessageBarBody, MessageBarTitle,
  Checkbox, Link as FLink, Textarea,
  Dialog, DialogTrigger, DialogSurface, DialogTitle, DialogBody, DialogContent, DialogActions
} from '@fluentui/react-components'
import { AppConfig, Lakehouse, Table, Run, SpecResponse } from './types'
import { api, getFabricToken, getOnelakeToken, inFabric, fabricWorkspaceId, fabricItemId } from './api'

const useStyles = makeStyles({
  shell: { maxWidth: '960px', margin: '0 auto', padding: '24px', display: 'flex', flexDirection: 'column' as const, gap: '14px' },
  headerBar: { display: 'flex', alignItems: 'center', gap: '14px', paddingBottom: '8px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, marginBottom: '6px' },
  headerTitle: { flex: 1 },
  logo: { height: '168px', width: 'auto' },
  row: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' as const },
  status: { padding: '12px 16px', backgroundColor: tokens.colorNeutralBackground2, borderRadius: '6px', lineHeight: 1.7 },
  tables: { maxHeight: '260px', overflow: 'auto', padding: '8px 12px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: '4px' },
  timeline: { display: 'flex', flexDirection: 'column' as const, gap: '8px', marginTop: '12px' },
  step: { display: 'flex', alignItems: 'flex-start', gap: '10px', padding: '10px 12px', borderRadius: '6px' },
  stepActive: { backgroundColor: tokens.colorBrandBackground2 },
  stepDone: { opacity: 0.85 },
  stepPending: { opacity: 0.5 },
  stepFail: { backgroundColor: tokens.colorPaletteRedBackground2 },
  stepIcon: { fontSize: '18px', lineHeight: '20px', width: '20px', textAlign: 'center' as const },
  stepBody: { display: 'flex', flexDirection: 'column' as const, gap: '2px', flex: 1 },
  stepTitle: { fontWeight: 600 },
  stepSub: { fontSize: '12px', color: tokens.colorNeutralForeground3 }
})

// Split a combined spec markdown into the 4 editable sections by H2 heading.
// Anything before the first matched section heading is "header" (preserved as-is).
const SECTION_PATTERNS: Record<string, RegExp> = {
  generic: /^##\s+Generic guidance\s*$/im,
  bronze: /^##\s+Bronze\s*$/im,
  silver: /^##\s+Silver\s*$/im,
  gold: /^##\s+Gold\s*$/im,
  semantic: /^##\s+Semantic model\s*$/im,
  report: /^##\s+Report\s*$/im,
  agent: /^##\s+Data Agent\s*$/im,
}
// Note: '## Updated specs' is intentionally NOT in SECTION_PATTERNS so it lands in the
// 'header' chunk along with '# Run Spec' / '## Inputs'. It's displayed as a banner above
// the editable section accordions on the next render.
function splitSpec(spec: string): { header: string; generic: string; bronze: string; silver: string; gold: string; semantic: string; report: string; agent: string } {
  const empty = { header: '', generic: '', bronze: '', silver: '', gold: '', semantic: '', report: '', agent: '' }
  if (!spec) return empty
  const lines = spec.split(/\r?\n/)
  const slots: Array<{ key: string; line: number }> = []
  lines.forEach((line, i) => {
    for (const [key, rx] of Object.entries(SECTION_PATTERNS)) {
      if (rx.test(line)) slots.push({ key, line: i })
    }
  })
  slots.sort((a, b) => a.line - b.line)
  if (slots.length === 0) return { ...empty, header: spec }
  const header = lines.slice(0, slots[0].line).join('\n').trim()
  const out: Record<string, string> = { generic: '', bronze: '', silver: '', gold: '', semantic: '', report: '', agent: '' }
  for (let i = 0; i < slots.length; i++) {
    const start = slots[i].line
    const end = i + 1 < slots.length ? slots[i + 1].line : lines.length
    out[slots[i].key] = lines.slice(start, end).join('\n').trim()
  }
  return { header, generic: out.generic, bronze: out.bronze, silver: out.silver, gold: out.gold, semantic: out.semantic, report: out.report, agent: out.agent }
}
function joinSpec(parts: { header: string; generic: string; bronze: string; silver: string; gold: string; semantic: string; report: string; agent: string }): string {
  return [parts.header, parts.generic, parts.bronze, parts.silver, parts.gold, parts.semantic, parts.report, parts.agent].filter(s => s && s.length).join('\n\n').trim() + '\n'
}

function signatureFromTrace(trace: string): string {
  // Strip volatile bits (run IDs, timestamps, line numbers, memory addresses, GUIDs) so
  // two traces from "the same root cause" compare equal.
  return trace
    .replace(/\d{4}-\d{2}-\d{2}T[\d:.]+Z?/g, '<TS>')
    .replace(/RUN=[\w-]+/g, 'RUN=<id>')
    .replace(/[a-f0-9-]{36}/g, '<guid>')
    .replace(/line \d+/g, 'line <n>')
    .replace(/#\d+/g, '#<n>')
    .replace(/ipykernel_\d+/g, 'ipykernel_<pid>')
    .replace(/\d{3,}/g, '<num>')
    .trim()
}

type BuildStep = {
  key: string
  title: string
  sub?: string
  state: 'pending' | 'active' | 'done' | 'failed'
}

function formatElapsed(ms: number): string {
  if (!isFinite(ms) || ms < 0) ms = 0
  const totalSec = Math.floor(ms / 1000)
  const h = Math.floor(totalSec / 3600)
  const m = Math.floor((totalSec % 3600) / 60)
  const sec = totalSec % 60
  const pad = (n: number) => n.toString().padStart(2, '0')
  return h > 0 ? `${h}:${pad(m)}:${pad(sec)}` : `${m}:${pad(sec)}`
}

function statusToSteps(status: string, run: Run, model: string | null): BuildStep[] {
  const s = status
  const failed = s === 'Failed' || s === 'Cancelled'
  const succeeded = s === 'Succeeded'
  const hasLh = !!run.targetLakehouseId
  const hasBronzeNb = !!run.bronzeNotebookId
  const hasSilverNb = !!run.silverNotebookId
  const hasGoldNb = !!run.goldNotebookId
  const hasReportingNb = !!run.reportingNotebookId
  const hasBronzeJob = !!run.bronzeJobId
  const hasSilverJob = !!run.silverJobId
  const hasGoldJob = !!run.goldJobId
  const hasReportingJob = !!run.reportingJobId

  // For per-layer step state, look at status + whether the IDs have been recorded.
  type Layer = 'bronze'|'silver'|'gold'|'reporting'
  const layerOrder: Record<Layer, number> = { bronze: 0, silver: 1, gold: 2, reporting: 3 }
  const layerState = (layer: Layer): BuildStep['state'] => {
    const hasNb = layer === 'bronze' ? hasBronzeNb : layer === 'silver' ? hasSilverNb : layer === 'gold' ? hasGoldNb : hasReportingNb
    const hasJob = layer === 'bronze' ? hasBronzeJob : layer === 'silver' ? hasSilverJob : layer === 'gold' ? hasGoldJob : hasReportingJob
    const cap = layer.charAt(0).toUpperCase() + layer.slice(1)
    const isDeploying = s === `Deploying${cap}`
    const isRunning = s === `Running${cap}`
    if (succeeded) return 'done'
    if (failed && run.currentLayer === layer) return 'failed'
    if (isDeploying || isRunning) return 'active'
    if (hasJob && run.currentLayer && layerOrder[layer] < layerOrder[run.currentLayer as Layer]) return 'done'
    if (hasJob) return 'done' // best-effort fallback
    return 'pending'
  }

  const lakehouseState: BuildStep['state'] = (() => {
    if (succeeded) return 'done'
    if (failed && !hasLh) return 'failed'
    if (['CreatingLakehouse','ReusingLakehouse'].includes(s)) return 'active'
    return hasLh ? 'done' : 'pending'
  })()
  const genState: BuildStep['state'] = (() => {
    if (succeeded) return 'done'
    if (s === 'GeneratingNotebook') return 'active'
    return (hasBronzeNb || hasSilverNb || hasGoldNb || hasReportingNb) ? 'done' : 'pending'
  })()

  const lakehouseShortName = (() => {
    if (!run.targetLakehouseName) return 'medallion'
    return run.targetLakehouseName.replace(/[^A-Za-z0-9_]+/g, '_').replace(/^_+|_+$/g, '') || 'medallion'
  })()

  return [
    { key: 'lh', title: s === 'ReusingLakehouse' ? 'Reusing target Lakehouse' : 'Creating target Lakehouse',
      sub: run.targetLakehouseName ?? undefined,
      state: lakehouseState },
    { key: 'gen', title: 'Generating 4 PySpark notebooks (bronze/silver/gold/reporting)',
      sub: model ? `LLM: ${model}` : undefined,
      state: genState },
    { key: 'bronze', title: 'Bronze layer notebook',
      sub: run.bronzeNotebookId ? `${lakehouseShortName}_bronze` : undefined,
      state: layerState('bronze') },
    { key: 'silver', title: 'Silver layer notebook',
      sub: run.silverNotebookId ? `${lakehouseShortName}_silver` : undefined,
      state: layerState('silver') },
    { key: 'gold', title: 'Gold layer notebook (+ data quality tests)',
      sub: run.goldNotebookId ? `${lakehouseShortName}_gold` : undefined,
      state: layerState('gold') },
    { key: 'reporting', title: 'Reporting notebook (semantic model + report + data agent)',
      sub: run.reportingNotebookId ? `${lakehouseShortName}_reporting` : undefined,
      state: layerState('reporting') },
    { key: 'done', title: 'Done',
      sub: succeeded ? 'All tables written; reports created' : undefined,
      state: succeeded ? 'done' : 'pending' }
  ]
}

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
  // Per-section editable specs (parsed/split from specDraft)
  const [specHeader, setSpecHeader] = useState('')
  const [specGeneric, setSpecGeneric] = useState('')
  const [specBronze, setSpecBronze] = useState('')
  const [specSilver, setSpecSilver] = useState('')
  const [specGold, setSpecGold] = useState('')
  const [specSemantic, setSpecSemantic] = useState('')
  const [specReport, setSpecReport] = useState('')
  const [specAgent, setSpecAgent] = useState('')
  const [openSection, setOpenSection] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [busyMsg, setBusyMsg] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [specs, setSpecs] = useState<SpecResponse | null>(null)
  const [run, setRun] = useState<Run | null>(null)

  const [maxIterations, setMaxIterations] = useState(5)
  const [currentIteration, setCurrentIteration] = useState(1)
  const [autoFixing, setAutoFixing] = useState(false)
  const triedAutoFixForKey = useRef<Set<string>>(new Set())
  const lastErrorSignatureRef = useRef<string | null>(null)
  const [stuckOnSameError, setStuckOnSameError] = useState(false)
  const [nowTick, setNowTick] = useState(() => Date.now())
  useEffect(() => {
    // Tick once a second so the elapsed clock updates while a build is active.
    const id = setInterval(() => setNowTick(Date.now()), 1000)
    return () => clearInterval(id)
  }, [])
  const [guidanceOpen, setGuidanceOpen] = useState(false)
  const [guidanceItems, setGuidanceItems] = useState<{id:number; capturedAt:string; runId:string; content:string}[] | null>(null)
  const [guidanceLoading, setGuidanceLoading] = useState(false)

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
  const [sourceWorkspaceName, setSourceWorkspaceName] = useState<string | null>(null)
  const [targetLakehouseId, setTargetLakehouseId] = useState<string | null>(null)
  const [targetLakehouseName2, setTargetLakehouseName2] = useState<string | null>(null)
  const [targetWorkspaceId, setTargetWorkspaceId] = useState<string | null>(null)
  const [targetWorkspaceName, setTargetWorkspaceName] = useState<string | null>(null)
  const [itemWorkspaceName, setItemWorkspaceName] = useState<string | null>(null)

  // Resolve item workspace name (the workspace where the workload item lives).
  useEffect(() => {
    if (!fabricWorkspaceId) return
    ;(async () => {
      try {
        const { fabric } = await ensureToken()
        const r = await api<{ id: string; displayName: string }>(`/api/workspaces/${fabricWorkspaceId}`, fabric)
        if (r.displayName) setItemWorkspaceName(r.displayName)
      } catch { /* ignore */ }
    })()
  }, [fabricWorkspaceId])

  const [availableModels, setAvailableModels] = useState<string[]>([])
  const [selectedModel, setSelectedModel] = useState<string | null>(null)
  useEffect(() => {
    fetch('/api/models')
      .then(r => r.ok ? r.json() : null)
      .then((j: { models: string[]; defaultModel: string } | null) => {
        if (!j) return
        setAvailableModels(j.models || [])
        setSelectedModel(j.defaultModel || (j.models && j.models[0]) || null)
      })
      .catch(() => { /* ignore */ })
  }, [])

  // Inside Fabric: receive picker results from the workload host.
  useEffect(() => {
    if (!inFabricRuntime) return
    function handler(ev: MessageEvent) {
      if (ev.data?.type === 'copilot-medallion-source-picked') {
        const sw = ev.data.workspaceId || null
        setSourceWorkspaceOverride(sw)
        setSourceWorkspaceName(ev.data.workspaceName || null)
        ;(window as any).__copilotMedallionSourceWs = sw
        const lhId = ev.data.lakehouseId || null
        setSourceId(lhId)
        setSourceLakehouseName(ev.data.lakehouseName || null)
        // Back-fill any missing names by asking the backend.
        if (sw && !ev.data.workspaceName) {
          ;(async () => {
            try {
              const { fabric } = await ensureToken()
              const r = await api<{ id: string; displayName: string | null }>(`/api/workspaces/${sw}`, fabric)
              if (r.displayName) setSourceWorkspaceName(r.displayName)
            } catch { /* ignore */ }
          })()
        }
        if (sw && lhId && !ev.data.lakehouseName) {
          ;(async () => {
            try {
              const { fabric } = await ensureToken()
              const r = await api<{ id: string; displayName: string }>(`/api/sources/lakehouses/${lhId}?workspaceId=${sw}`, fabric)
              if (r.displayName) setSourceLakehouseName(r.displayName)
            } catch { /* ignore */ }
          })()
        }
        const tabs: string[] = Array.isArray(ev.data.tables) ? ev.data.tables : []
        const schemas: string[] = Array.isArray(ev.data.schemas) ? ev.data.schemas : []
        if (tabs.length === 0 && ev.data.onlyRoot && lhId) {
          // User picked the root "Tables" node — enumerate all tables in that lakehouse.
          setBusy(true); setBusyMsg('Enumerating tables in source lakehouse...')
          ensureToken().then(({ fabric, onelake }) =>
            api<Table[]>(`/api/sources/lakehouses/${lhId}/tables`, fabric, undefined, onelake)
              .then(list => {
                const names = list.map(t => t.name)
                setTables(list)
                setSelectedTables(new Set(names))
              })
              .catch(e => setError(String(e)))
              .finally(() => { setBusy(false); setBusyMsg('') })
          )
        } else if (schemas.length > 0) {
          // User picked one or more SCHEMA folders — expand to all tables in those schemas.
          setBusy(true); setBusyMsg(`Expanding ${schemas.length} schema${schemas.length === 1 ? '' : 's'}...`)
          ensureToken().then(({ fabric, onelake }) =>
            api<Table[]>(`/api/sources/lakehouses/${lhId}/tables`, fabric, undefined, onelake)
              .then(list => {
                // Tables come back as "schema/table" — filter to selected schemas + add explicit table picks.
                const fromSchemas = list
                  .map(t => t.name)
                  .filter(n => schemas.some(s => n === s || n.startsWith(s + '/')))
                const combined = Array.from(new Set([...tabs, ...fromSchemas]))
                setTables(combined.map(n => ({ name: n })))
                setSelectedTables(new Set(combined))
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
        setTargetWorkspaceName(ev.data.workspaceName || null)
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

  // Fetch persisted Spark error traceback once a run lands in Failed.
  const [sparkError, setSparkError] = useState<string | null>(null)
  useEffect(() => {
    if (!run) { setSparkError(null); return }
    if (run.status !== 'Failed' && run.status !== 'Cancelled') { setSparkError(null); return }
    if (!run.targetLakehouseId) return
    let cancelled = false
    ;(async () => {
      try {
        const { fabric, onelake } = await ensureToken()
        console.log('[sparkError] fetching error.txt, onelakeToken present=', !!onelake)
        const resp = await api<{ error: string | null }>(`/api/runs/${run.runId}/error`, fabric, undefined, onelake)
        console.log('[sparkError] response error length=', resp.error?.length ?? 0)
        if (!cancelled && resp.error) setSparkError(resp.error)
      } catch (e) { console.warn('[sparkError] fetch failed', e) }
    })()
    return () => { cancelled = true }
  }, [run?.runId, run?.status, run?.targetLakehouseId])

  async function loadGuidance() {
    setGuidanceLoading(true)
    try {
      const { fabric } = await ensureToken()
      const items = await api<{id:number; capturedAt:string; runId:string; content:string}[]>(
        '/api/guidance?limit=100', fabric)
      setGuidanceItems(items)
    } catch (e: any) {
      setGuidanceItems([])
      setError(`Failed to load guidance history: ${e?.message ?? e}`)
    } finally {
      setGuidanceLoading(false)
    }
  }

  const autoProposedForKeyRef = useRef<string | null>(null)

  async function previewSpecs() {
    if (!sourceId || selectedTables.size === 0) return
    setError(null); setBusy(true); setBusyMsg(`Reading source schemas and asking ${selectedModel ?? 'AI'} to propose a spec...`)
    try {
      const { fabric, onelake } = await ensureToken()
      const resp = await api<{ markdown: string; runId: string; targetLakehouseName: string }>(
        '/api/specs/preview', fabric, {
          method: 'POST',
          body: JSON.stringify({
            sourceLakehouseId: sourceId,
            tables: Array.from(selectedTables),
            targetLakehouseName: targetName || null,
            model: selectedModel
          })
        }, onelake)
      setSpecDraft(resp.markdown)
      setPreviewRunId(resp.runId)
      if (!targetName) setTargetName(resp.targetLakehouseName)
    } catch (e: any) { setError(String(e)) }
    finally { setBusy(false); setBusyMsg('') }
  }

  // Auto-propose: as soon as the user has chosen a model + source + at least one table,
  // call the LLM in the background and fill the spec editor. No "Propose Specs" click needed.
  useEffect(() => {
    if (!effectivelySignedIn) return
    if (!sourceId || selectedTables.size === 0) return
    if (!selectedModel) return
    if (run || specDraft || busy) return
    const key = `${selectedModel}|${sourceId}|${[...selectedTables].sort().join(',')}`
    if (autoProposedForKeyRef.current === key) return
    autoProposedForKeyRef.current = key
    previewSpecs().catch(e => console.warn('[auto-propose] failed', e))
    // intentionally exclude `busy` from deps — we only want to fire on selection changes
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [effectivelySignedIn, sourceId, selectedTables, selectedModel, run, specDraft])

  async function saveAndBuild() {
    if (!sourceId || selectedTables.size === 0) return
    setError(null); setBusy(true); setBusyMsg('Saving spec to lakehouse Files/spec.md…')
    // Fresh build → reset iteration tracking so auto-fix gets a full budget.
    setCurrentIteration(1)
    triedAutoFixForKey.current.clear()
    lastErrorSignatureRef.current = null
    setStuckOnSameError(false)
    setSparkError(null)
    try {
      const combined = recombineSpec()
      const { fabric } = await ensureToken()
      const resp = await api<SpecResponse>('/api/specs', fabric, {
        method: 'POST',
        body: JSON.stringify({
          sourceLakehouseId: sourceId,
          tables: Array.from(selectedTables),
          targetLakehouseName: targetName || null,
          targetLakehouseId: targetLakehouseId || null,
          targetWorkspaceId: targetWorkspaceId || null,
          specMarkdown: combined || null,
          existingRunId: run?.runId ?? null,
        })
      })
      setSpecs(resp)
      setBusyMsg('Creating lakehouse + starting orchestrator notebook…')
      const r = await api<Run>('/api/build', fabric, {
        method: 'POST',
        body: JSON.stringify({ runId: resp.runId, model: selectedModel })
      })
      setRun(r)
    } catch (e: any) { setError(String(e)) }
    finally { setBusy(false); setBusyMsg('') }
  }

  async function continueBuild() {
    if (!run) return
    setError(null); setBusy(true); setBusyMsg('Creating lakehouse + starting orchestrator notebook...')
    // Manual rebuild click → reset iteration tracking so auto-fix gets another full budget.
    setCurrentIteration(1)
    triedAutoFixForKey.current.clear()
    lastErrorSignatureRef.current = null
    setStuckOnSameError(false)
    setSparkError(null)
    try {
      const { fabric } = await ensureToken()
      const r = await api<Run>('/api/build', fabric, {
        method: 'POST',
        body: JSON.stringify({ runId: run.runId, model: selectedModel })
      })
      setRun(r)
    } catch (e: any) { setError(String(e)) }
    finally { setBusy(false); setBusyMsg('') }
  }

  async function fixSpec() {
    if (!run || !sparkError) return
    if (!specDraft) {
      setError('No spec to fix — open the spec editor first.')
      return
    }
    setError(null); setBusy(true); setBusyMsg(`Asking ${selectedModel ?? 'AI'} to revise the spec...`)
    try {
      const combined = recombineSpec()
      const { fabric } = await ensureToken()
      const r = await api<{ markdown: string }>(`/api/runs/${run.runId}/fix-spec`, fabric, {
        method: 'POST',
        body: JSON.stringify({ currentSpec: combined, errorTrace: sparkError, model: selectedModel, iteration: currentIteration, failedLayer: run.currentLayer })
      })
      if (r.markdown) {
        setSpecDraft(r.markdown)
        setTimeout(() => {
          // Open the medallion section so the user can see the diff (most fixes target gold)
          setOpenSection('medallion')
        }, 100)
      }
    } catch (e: any) { setError(String(e)) }
    finally { setBusy(false); setBusyMsg('') }
  }

  // Full auto-fix-and-rebuild cycle: ask LLM to revise spec, push it, rebuild.
  // Returns true on success (build kicked off), false on failure.
  async function autoFixAndRebuild(): Promise<boolean> {
    if (!run || !sparkError) return false
    const combined = recombineSpec()
    if (!combined) return false
    const { fabric } = await ensureToken()
    setBusyMsg(`Iteration ${currentIteration + 1}/${maxIterations}: revising spec...`)
    const fixResp = await api<{ markdown: string }>(`/api/runs/${run.runId}/fix-spec`, fabric, {
      method: 'POST',
      body: JSON.stringify({ currentSpec: combined, errorTrace: sparkError, model: selectedModel, iteration: currentIteration, failedLayer: run.currentLayer })
    })
    if (!fixResp.markdown) throw new Error('No revised spec returned from the LLM.')
    setSpecDraft(fixResp.markdown)

    setBusyMsg(`Iteration ${currentIteration + 1}/${maxIterations}: pushing updated spec...`)
    await api<SpecResponse>('/api/specs', fabric, {
      method: 'POST',
      body: JSON.stringify({
        sourceLakehouseId: sourceId,
        tables: Array.from(selectedTables),
        targetLakehouseName: targetName || null,
        targetLakehouseId: targetLakehouseId || null,
        targetWorkspaceId: targetWorkspaceId || null,
        specMarkdown: fixResp.markdown,
        existingRunId: run.runId,
      })
    })

    setBusyMsg(`Iteration ${currentIteration + 1}/${maxIterations}: rebuilding...`)
    const r = await api<Run>('/api/build', fabric, {
      method: 'POST',
      body: JSON.stringify({ runId: run.runId, model: selectedModel })
    })
    setSparkError(null)
    setCurrentIteration(currentIteration + 1)
    setRun(r)
    return true
  }

  // Whenever specDraft changes (from preview/restore), split into 4 sections.
  // When any section changes, recombine into specDraft so existing build flow keeps working.
  useEffect(() => {
    const parts = splitSpec(specDraft)
    setSpecHeader(parts.header)
    setSpecGeneric(parts.generic)
    setSpecBronze(parts.bronze)
    setSpecSilver(parts.silver)
    setSpecGold(parts.gold)
    setSpecSemantic(parts.semantic)
    setSpecReport(parts.report)
    setSpecAgent(parts.agent)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [specDraft])

  function recombineSpec() {
    const combined = joinSpec({
      header: specHeader,
      generic: specGeneric,
      bronze: specBronze,
      silver: specSilver,
      gold: specGold,
      semantic: specSemantic,
      report: specReport,
      agent: specAgent,
    })
    setSpecDraft(combined)
    return combined
  }

  // Auto-iterate: when a run lands in Failed with a captured traceback,
  // and we still have iterations left, automatically fix + rebuild.
  // BUT: if two consecutive iterations hit the EXACT same error signature, stop —
  // the LLM is stuck and human input is needed.
  useEffect(() => {
    if (!run || !sparkError) {
      console.log('[autoFix] skip: run=', !!run, 'sparkError=', !!sparkError)
      return
    }
    if (run.status !== 'Failed' && run.status !== 'Cancelled') {
      console.log('[autoFix] skip: status=', run.status)
      return
    }
    if (currentIteration >= maxIterations) {
      console.log('[autoFix] skip: iterations exhausted', currentIteration, maxIterations)
      return
    }
    if (autoFixing) {
      console.log('[autoFix] skip: already auto-fixing')
      return
    }
    if (stuckOnSameError) {
      console.log('[autoFix] skip: stuck on same error')
      return
    }
    const key = `${run.runId}::iter${currentIteration}`
    if (triedAutoFixForKey.current.has(key)) {
      console.log('[autoFix] skip: already tried', key)
      return
    }
    triedAutoFixForKey.current.add(key)

    const signature = signatureFromTrace(sparkError)
    if (lastErrorSignatureRef.current && lastErrorSignatureRef.current === signature) {
      console.log('[autoFix] STUCK: signature matches previous iteration')
      setStuckOnSameError(true)
      return
    }
    lastErrorSignatureRef.current = signature
    console.log('[autoFix] starting iteration', currentIteration + 1, 'of', maxIterations)

    setAutoFixing(true)
    setError(null); setBusy(true)
    autoFixAndRebuild()
      .catch(e => { console.error('[autoFix] failed', e); setError(`Auto-fix failed: ${String(e)}`) })
      .finally(() => { setAutoFixing(false); setBusy(false); setBusyMsg('') })
  }, [run?.runId, run?.status, sparkError, currentIteration, maxIterations, stuckOnSameError])

  // Reset iteration counter whenever the user starts a fresh manual build.
  useEffect(() => {
    if (run?.status === 'Succeeded') {
      // success — keep counter for display; the user can start a new run by reloading
    }
    if (!run) {
      setCurrentIteration(1)
      triedAutoFixForKey.current.clear()
    }
  }, [run?.runId])

  return (
    <div className={s.shell}>
      <div className={s.headerBar}>
        <div className={s.headerTitle}>
          <Title1>🛠️ Copilot Medallion</Title1>
          <Body1>Automated Bronze → Silver → Gold + semantic model + report + data agent for Microsoft Fabric.</Body1>
          <Caption1>
            {itemWorkspaceName ? <>Workspace: <strong>{itemWorkspaceName}</strong></> : (fabricWorkspaceId ? <>Workspace: <code>{fabricWorkspaceId}</code></> : null)}
            {signedIn && <> · <FLink onClick={signOut} as="button" style={{ background: 'none', border: 'none', cursor: 'pointer' }}>Sign out</FLink></>}
            {effectivelySignedIn && <> · <FLink onClick={() => { setGuidanceOpen(true); if (!guidanceItems) loadGuidance() }} as="button" style={{ background: 'none', border: 'none', cursor: 'pointer' }}>📚 Guidance history</FLink></>}
          </Caption1>
        </div>
        <a href="https://github.com/Remc0000" target="_blank" rel="noreferrer" title="Remc0000 on GitHub">
          <img src="/logo.png" alt="Remc0000" className={s.logo} />
        </a>
      </div>

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
          <CardHeader
            header={<Title3>1. Pick model</Title3>}
            description={<Body1>Which LLM should propose specs and generate the notebooks?</Body1>}
          />
          <div className={s.row}>
            <Label htmlFor="mdl-top">Notebook-generation model</Label>
            <Dropdown
              id="mdl-top"
              value={selectedModel ?? ''}
              selectedOptions={selectedModel ? [selectedModel] : []}
              onOptionSelect={(_, d) => setSelectedModel(d.optionValue ?? null)}
              disabled={!!run}
            >
              {availableModels.map(m => (
                <Option key={m} value={m}>{m}</Option>
              ))}
            </Dropdown>
          </div>
          <div className={s.row}>
            <Label htmlFor="iter-top">Max auto-fix iterations</Label>
            <Input
              id="iter-top"
              type="number"
              min={1}
              max={20}
              value={String(maxIterations)}
              onChange={(_, d) => {
                const n = parseInt(d.value || '5', 10)
                if (!isNaN(n) && n >= 1 && n <= 20) setMaxIterations(n)
              }}
              style={{ width: 80 }}
              disabled={!!run}
            />
            <Caption1>On failure, automatically ask the LLM to fix the spec and re-run, up to this many attempts.</Caption1>
          </div>
        </Card>
      )}

      {effectivelySignedIn && (
        <Card>
          <CardHeader header={<Title3>2. Pick source &amp; tables</Title3>} />
          {inFabricRuntime ? (
            sourceId ? (
              <div className={s.status}>
                <div>Source Lakehouse: <strong>{sourceLakehouseName ?? '(unknown)'}</strong></div>
                <div>Source Workspace: <strong>{sourceWorkspaceName ?? '(unknown)'}</strong></div>
                <div>{selectedTables.size} table{selectedTables.size === 1 ? '' : 's'} selected</div>
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
          {(sourceId && selectedTables.size > 0) && null}
        </Card>
      )}

      {effectivelySignedIn && sourceId && selectedTables.size > 0 && (
        <Card>
          <CardHeader header={<Title3>3. Choose target Lakehouse</Title3>} description={<Body1>Pick an existing Lakehouse via the top-bar <b>Pick target Lakehouse…</b> button, or just type a name below to create a fresh one.</Body1>} />
          {targetLakehouseId ? (
            <div className={s.row}>
              <Caption1>
                Using existing target: <strong>{targetLakehouseName2 ?? '(unknown)'}</strong>
                {targetWorkspaceId && targetWorkspaceId !== fabricWorkspaceId && <> · workspace <strong>{targetWorkspaceName ?? '(unknown)'}</strong></>}
              </Caption1>
              {!run && (
                <Button disabled={busy} onClick={() => {
                  setTargetLakehouseId(null); setTargetWorkspaceId(null); setTargetWorkspaceName(null); setTargetLakehouseName2(null)
                  ;(window as any).__copilotMedallionTargetWs = null
                  setTargetName('')
                }}>Clear</Button>
              )}
            </div>
          ) : (
            <div className={s.row}>
              <Label htmlFor="tn">Target Lakehouse name (creates new)</Label>
              <Input id="tn" value={targetName} onChange={(_, d) => setTargetName(d.value)} placeholder="(auto-generated)" disabled={!!run} />
            </div>
          )}
        </Card>
      )}

      {effectivelySignedIn && sourceId && selectedTables.size > 0 && (
        <Card>
          <CardHeader header={<Title3>4. Review &amp; build</Title3>} />
          {!specDraft ? (
            <div className={s.row}>
              <Spinner size="tiny" label={busy ? busyMsg : `Asking ${selectedModel ?? 'AI'} to analyse your tables and propose a spec…`} />
            </div>
          ) : (
            <>
              <div className={s.row}>
                <Button disabled={busy} onClick={previewSpecs} icon={<span>✨</span>}>
                  Re-propose with AI (overwrites edits)
                </Button>
                <Caption1>or click any section below to edit it directly</Caption1>
                {busy && <Spinner size="tiny" label={busyMsg} />}
              </div>
              <Caption1>
                {previewRunId && <>Spec for run <code>{previewRunId}</code></>}
                {run && <>Current run <code>{run.runId}</code></>}
                {' · click a section to edit'}
              </Caption1>

              {/* Changelog banner: surfaces "## Updated specs" entries the LLM prepends after auto-fix iterations */}
              {specHeader && /^##\s+Updated specs/im.test(specHeader) && (() => {
                const m = specHeader.match(/##\s+Updated specs[\s\S]*$/i)
                if (!m) return null
                return (
                  <div style={{ border: `1px solid ${tokens.colorPaletteYellowBorder1}`, background: tokens.colorPaletteYellowBackground1, padding: '10px 14px', borderRadius: 6, marginBottom: 4 }}>
                    <details>
                      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>📝 Spec change log (click to expand)</summary>
                      <pre style={{ fontSize: 11, marginTop: 8, maxHeight: 260, overflow: 'auto', whiteSpace: 'pre-wrap' }}>{m[0]}</pre>
                    </details>
                  </div>
                )
              })()}

              {/* Seven collapsible spec sections */}
              {[
                { key: 'generic',   icon: '🧭', label: 'Generic guidance (no need to edit — LLM maintains)', value: specGeneric,   setter: setSpecGeneric,   placeholder: '## Generic guidance\n\nWhich skills/agents should the LLM follow? Cross-cutting rules…' },
                { key: 'bronze',    icon: '🥉', label: 'Bronze (raw ingestion)',                          value: specBronze,    setter: setSpecBronze,    placeholder: '## Bronze\n\nHow each source table is landed: metadata columns, write mode, partitioning…' },
                { key: 'silver',    icon: '🥈', label: 'Silver (cleaning & dedup)',                       value: specSilver,    setter: setSpecSilver,    placeholder: '## Silver\n\nDedup, snake_case, fully-null drop, audit columns, OPTIMIZE…' },
                { key: 'gold',      icon: '🥇', label: 'Gold (dims/facts + data quality tests)',          value: specGold,      setter: setSpecGold,      placeholder: '## Gold\n\nDims, facts, joins, measures-supporting columns, data quality tests…' },
                { key: 'semantic',  icon: '🧊', label: 'Semantic Model (Direct Lake)',                    value: specSemantic,  setter: setSpecSemantic,  placeholder: '## Semantic model\n\nDescribe tables, relationships, measures…' },
                { key: 'report',    icon: '📊', label: 'Power BI Report',                                 value: specReport,    setter: setSpecReport,    placeholder: '## Report\n\nDescribe pages, visuals, filters…' },
                { key: 'agent',     icon: '🤖', label: 'Data Agent (AISkill on the semantic model)',      value: specAgent,     setter: setSpecAgent,     placeholder: '## Data Agent\n\nDescribe role, instructions, starter questions, guardrails…' },
              ].map(sec => {
                const isOpen = openSection === sec.key
                const hasContent = !!(sec.value && sec.value.trim())
                return (
                  <div key={sec.key} style={{ border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: 6, marginBottom: 8 }}>
                    <div
                      onClick={() => setOpenSection(isOpen ? null : sec.key)}
                      style={{ padding: '10px 14px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', cursor: 'pointer', background: isOpen ? tokens.colorBrandBackground2 : 'transparent', borderBottom: isOpen ? `1px solid ${tokens.colorNeutralStroke2}` : 'none' }}
                    >
                      <span style={{ fontWeight: 600 }}>
                        {sec.icon} {sec.label}
                        {hasContent && <span style={{ marginLeft: 8, fontSize: 11, color: tokens.colorPaletteGreenForeground1 }}>✓ {sec.value.trim().split(/\s+/).length} words</span>}
                        {!hasContent && <span style={{ marginLeft: 8, fontSize: 11, color: tokens.colorNeutralForeground3 }}>(empty — click to write)</span>}
                      </span>
                      <span style={{ fontSize: 12, color: tokens.colorNeutralForeground3 }}>{isOpen ? '▼ collapse' : '▶ edit'}</span>
                    </div>
                    {isOpen && (
                      <div style={{ padding: 12 }}>
                        <Textarea
                          value={sec.value}
                          onChange={(_, d) => sec.setter(d.value)}
                          placeholder={sec.placeholder}
                          rows={14}
                          resize="vertical"
                          style={{ fontFamily: 'monospace', fontSize: 12, width: '100%' }}
                        />
                      </div>
                    )}
                  </div>
                )
              })}

              {availableModels.length > 1 && (
                <Caption1>Model: <code>{selectedModel}</code> · change in section 1 if needed.</Caption1>
              )}
              <div className={s.row}>
                <Button appearance="primary" disabled={busy} onClick={saveAndBuild}>
                  💾 Save Specs &amp; Build
                </Button>
                {busy && <Spinner size="tiny" label={busyMsg} />}
              </div>
              <Caption1>
                Saved into the target lakehouse at <code>Files/spec.md</code> (and tracked in the runs database).
                {' '}A copy is also pushed to GitHub for history when <code>GITHUB_PAT</code> is configured.
                {' '}Build starts immediately after save — no extra click needed.
              </Caption1>
            </>
          )}
        </Card>
      )}

      {run && (
        <Card>
          <CardHeader
            header={<Title3>5. Build status</Title3>}
            description={(() => {
              const startedMs = run.createdAt ? Date.parse(run.createdAt) : NaN
              const isTerminal = ['Succeeded','Failed','Cancelled'].includes(run.status)
              const endMs = isTerminal && run.updatedAt ? Date.parse(run.updatedAt) : nowTick
              const elapsed = isFinite(startedMs) ? endMs - startedMs : 0
              return (
                <Body1>
                  Status: <strong>{run.status}</strong>
                  {selectedModel ? <> · model <code>{selectedModel}</code></> : null}
                  <> · iteration <strong>{currentIteration}</strong> of <strong>{maxIterations}</strong></>
                  {autoFixing ? <> · <em>auto-fixing…</em></> : null}
                  {isFinite(startedMs) && (
                    <> · {isTerminal ? 'took' : 'elapsed'} <strong>⏱ {formatElapsed(elapsed)}</strong></>
                  )}
                </Body1>
              )
            })()}
          />
          <div className={s.status}>
            <div>Run: <code>{run.runId}</code></div>
            <div>Target Lakehouse: <strong>{run.targetLakehouseName ?? '(pending)'}</strong></div>
            <div>Workspace: <strong>{itemWorkspaceName ?? '(resolving…)'}</strong></div>
            {run.bronzeNotebookId && <div>Bronze notebook: <code>{(run.targetLakehouseName ?? 'medallion').replace(/[^A-Za-z0-9_]+/g,'_').replace(/^_+|_+$/g,'')}_bronze</code></div>}
            {run.silverNotebookId && <div>Silver notebook: <code>{(run.targetLakehouseName ?? 'medallion').replace(/[^A-Za-z0-9_]+/g,'_').replace(/^_+|_+$/g,'')}_silver</code></div>}
            {run.goldNotebookId && <div>Gold notebook: <code>{(run.targetLakehouseName ?? 'medallion').replace(/[^A-Za-z0-9_]+/g,'_').replace(/^_+|_+$/g,'')}_gold</code></div>}
            {run.reportingNotebookId && <div>Reporting notebook: <code>{(run.targetLakehouseName ?? 'medallion').replace(/[^A-Za-z0-9_]+/g,'_').replace(/^_+|_+$/g,'')}_reporting</code></div>}
            <div>Spec: stored in lakehouse <code>Files/spec.md</code>{run.specUrl && run.specUrl.startsWith('http') ? <> · <FLink href={run.specUrl} target="_blank">history on GitHub</FLink></> : null}</div>
            {run.message && <div>Message: <code>{run.message}</code></div>}
          </div>
          <div className={s.timeline}>
            {statusToSteps(run.status, run, selectedModel).map(st => {
              const cls = [
                s.step,
                st.state === 'active' ? s.stepActive :
                st.state === 'done' ? s.stepDone :
                st.state === 'failed' ? s.stepFail :
                s.stepPending
              ].join(' ')
              const icon =
                st.state === 'done' ? '✓' :
                st.state === 'failed' ? '✕' :
                st.state === 'active' ? '●' :
                '○'
              return (
                <div key={st.key} className={cls}>
                  <div className={s.stepIcon}>
                    {st.state === 'active' ? <Spinner size="extra-tiny" /> : icon}
                  </div>
                  <div className={s.stepBody}>
                    <div className={s.stepTitle}>{st.title}</div>
                    {st.sub && <div className={s.stepSub}>{st.sub}</div>}
                  </div>
                </div>
              )
            })}
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
            <>
              <MessageBar intent="success">
                <MessageBarBody><MessageBarTitle>Done</MessageBarTitle> Medallion built across 3 notebooks (bronze/silver/gold). Edit the spec above and press <b>Re-build with updated spec</b> to iterate — same lakehouse + notebooks get updated in place.</MessageBarBody>
              </MessageBar>
              <div className={s.row}>
                {run.targetLakehouseId && run.workspaceId && (
                  <Button appearance="primary" onClick={() => window.open(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/lakehouses/${run.targetLakehouseId}`, '_blank', 'noopener,noreferrer')}>
                    Open Lakehouse
                  </Button>
                )}
                {run.bronzeNotebookId && run.workspaceId && (
                  <Button onClick={() => window.open(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${run.bronzeNotebookId}`, '_blank', 'noopener,noreferrer')}>
                    Open Bronze
                  </Button>
                )}
                {run.silverNotebookId && run.workspaceId && (
                  <Button onClick={() => window.open(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${run.silverNotebookId}`, '_blank', 'noopener,noreferrer')}>
                    Open Silver
                  </Button>
                )}
                {run.goldNotebookId && run.workspaceId && (
                  <Button onClick={() => window.open(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${run.goldNotebookId}`, '_blank', 'noopener,noreferrer')}>
                    Open Gold
                  </Button>
                )}
              </div>
            </>
          )}
          {(run.status === 'Failed' || run.status === 'Cancelled') && (
            <>
              <MessageBar intent={stuckOnSameError ? 'error' : (autoFixing || currentIteration < maxIterations ? 'warning' : 'error')}>
                <MessageBarBody>
                  <MessageBarTitle>
                    {stuckOnSameError
                      ? `Stuck on the same error — auto-fix paused`
                      : autoFixing
                        ? `Auto-fixing (iteration ${currentIteration + 1} of ${maxIterations})…`
                        : currentIteration < maxIterations
                          ? `Failed — will auto-fix next (iteration ${currentIteration + 1} of ${maxIterations})`
                          : `${run.status} after ${currentIteration} iteration${currentIteration === 1 ? '' : 's'}`}
                  </MessageBarTitle>
                  {' '}{run.message ?? ''}
                  {stuckOnSameError && (
                    <div style={{ marginTop: 8 }}>
                      The same error happened twice in a row, so the LLM appears stuck.
                      Please edit the spec above directly (or click <b>✨ Fix spec with AI (manual)</b> for another try), then press <b>💾 Save spec</b> + <b>Re-build with updated spec</b>.
                    </div>
                  )}
                  {sparkError && (
                    <details style={{ marginTop: 8 }} open>
                      <summary style={{ cursor: 'pointer' }}>Spark cell traceback</summary>
                      <pre style={{ fontSize: 11, marginTop: 6, maxHeight: 320, overflow: 'auto', whiteSpace: 'pre-wrap', background: tokens.colorNeutralBackground3, padding: 8, borderRadius: 4 }}>{sparkError}</pre>
                    </details>
                  )}
                </MessageBarBody>
              </MessageBar>
              <div className={s.row}>
                {sparkError && !autoFixing && (stuckOnSameError || currentIteration >= maxIterations) && (
                  <Button appearance="primary" disabled={busy} onClick={fixSpec}>
                    ✨ Fix spec with AI (manual)
                  </Button>
                )}
                {autoFixing && <Spinner size="tiny" label={busyMsg} />}
                {(() => {
                  const layerNb = run.currentLayer === 'gold' ? run.goldNotebookId
                                : run.currentLayer === 'silver' ? run.silverNotebookId
                                : run.currentLayer === 'bronze' ? run.bronzeNotebookId
                                : (run.goldNotebookId ?? run.silverNotebookId ?? run.bronzeNotebookId)
                  return layerNb && run.workspaceId && (
                    <Button onClick={() => window.open(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${layerNb}`, '_blank', 'noopener,noreferrer')}>
                      Open failed {run.currentLayer ?? ''} notebook
                    </Button>
                  )
                })()}
                {run.workspaceId && (
                  <Button onClick={() => window.open(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/list?experience=power-bi`, '_blank', 'noopener,noreferrer')}>
                    Open Workspace
                  </Button>
                )}
                <Button onClick={() => window.open('https://app.fabric.microsoft.com/monitoringhub?experience=power-bi', '_blank', 'noopener,noreferrer')}>
                  Open Monitoring Hub
                </Button>
              </div>
            </>
          )}
        </Card>
      )}

      <Dialog open={guidanceOpen} onOpenChange={(_, d) => setGuidanceOpen(d.open)}>
        <DialogSurface style={{ maxWidth: 900 }}>
          <DialogBody>
            <DialogTitle>📚 Generic guidance history</DialogTitle>
            <DialogContent>
              <Caption1>
                Every time you save a spec, its <code>## Generic guidance</code> section is snapshotted here so the rules accumulate across runs and lakehouses.
              </Caption1>
              <div style={{ marginTop: 12, maxHeight: '60vh', overflow: 'auto' }}>
                {guidanceLoading && <Spinner size="tiny" label="Loading..." />}
                {!guidanceLoading && guidanceItems && guidanceItems.length === 0 && (
                  <Body1>No snapshots yet. Save a spec to capture the first one.</Body1>
                )}
                {!guidanceLoading && guidanceItems && guidanceItems.map(g => (
                  <details key={g.id} style={{ marginBottom: 10, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: 6, padding: 8 }}>
                    <summary style={{ cursor: 'pointer', fontWeight: 600 }}>
                      {new Date(g.capturedAt).toLocaleString()} · <code style={{ fontSize: 11 }}>{g.runId}</code>
                    </summary>
                    <pre style={{ whiteSpace: 'pre-wrap', fontFamily: 'inherit', fontSize: 13, marginTop: 8 }}>{g.content}</pre>
                  </details>
                ))}
              </div>
            </DialogContent>
            <DialogActions>
              <Button onClick={() => loadGuidance()} disabled={guidanceLoading}>Refresh</Button>
              <DialogTrigger disableButtonEnhancement>
                <Button appearance="primary">Close</Button>
              </DialogTrigger>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  )
}
