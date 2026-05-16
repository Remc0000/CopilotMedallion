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
  headerTitle: { flex: 1, textAlign: 'center' as const },
  logo: { height: '156px', width: 'auto' },
  logoLeft: { height: '120px', width: 'auto' },
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
  indent?: boolean
}

function openExternal(url: string) {
  // Multi-strategy opener that works whether the page is loaded standalone, inside a
  // Fabric workload iframe (which often sandboxes window.open), or anywhere in between.
  try {
    // 1) Ask the outer Fabric host to open it — outer can call window.open from its
    //    own origin which is not sandbox-restricted. If the outer doesn't listen, this
    //    is a no-op and we fall through to strategy 2.
    if (window.parent && window.parent !== window) {
      window.parent.postMessage({ type: 'copilot-medallion-open-external', url }, '*')
    }
  } catch { /* ignore */ }
  // 2) Native window.open. If the browser allows popups for this iframe (most do when
  //    the call originates from a user gesture), the new tab opens.
  const w = window.open(url, '_blank', 'noopener,noreferrer')
  if (w) return
  // 3) Synthetic anchor click. This sometimes succeeds where window.open is denied
  //    because the browser treats it as an anchor activation, not a script-triggered popup.
  try {
    const a = document.createElement('a')
    a.href = url
    a.target = '_blank'
    a.rel = 'noopener noreferrer'
    document.body.appendChild(a)
    a.click()
    a.remove()
  } catch { /* ignore */ }
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
    { key: 'gen', title: 'Generating notebook cells with the LLM',
      sub: model ? `Model: ${model} · one call produces cells for all 4 layers` : 'One LLM call produces cells for all 4 layers',
      state: genState },
    { key: 'bronze', title: 'Bronze layer notebook (deploy + run)',
      sub: run.bronzeNotebookId ? `${lakehouseShortName}_bronze` : undefined,
      state: layerState('bronze'), indent: true },
    { key: 'silver', title: 'Silver layer notebook (deploy + run)',
      sub: run.silverNotebookId ? `${lakehouseShortName}_silver` : undefined,
      state: layerState('silver'), indent: true },
    { key: 'gold', title: 'Gold layer notebook (+ data quality tests)',
      sub: run.goldNotebookId ? `${lakehouseShortName}_gold` : undefined,
      state: layerState('gold'), indent: true },
    { key: 'reporting', title: 'Reporting notebook (semantic model + report + data agent)',
      sub: run.reportingNotebookId ? `${lakehouseShortName}_reporting` : undefined,
      state: layerState('reporting'), indent: true },
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
  const [run, setRun] = useState<Run | null>(null)

  const [maxIterations, setMaxIterations] = useState(5)
  const [currentIteration, setCurrentIteration] = useState(1)
  const [autoFixing, setAutoFixing] = useState(false)
  const triedAutoFixForKey = useRef<Set<string>>(new Set())
  const lastErrorSignatureRef = useRef<string | null>(null)
  const sameSignatureCountRef = useRef<number>(0)
  const [stuckOnSameError, setStuckOnSameError] = useState(false)
  const [autoFixRetryTick, setAutoFixRetryTick] = useState(0)
  const [nowTick, setNowTick] = useState(() => Date.now())
  const [usage, setUsage] = useState<{promptTokens:number; completionTokens:number; totalTokens:number; requests:number; estimatedCostUsd:number; perModel: {model:string; promptTokens:number; completionTokens:number; requests:number}[]} | null>(null)
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
        // Names that weren't included in the picker payload will be back-filled by the
        // resilient retry effect below (it watches sourceId + missing names).
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
          if (resp.run.sourceWorkspaceId) {
            setSourceWorkspaceOverride(resp.run.sourceWorkspaceId)
            ;(window as any).__copilotMedallionSourceWs = resp.run.sourceWorkspaceId
          }
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

  // Resilient back-fill: if sourceId is set but workspace/lakehouse names are still null
  // (e.g., the host iframe never sent them, or the previous back-fill 404'd), keep retrying
  // until both names are known. Bounded retries with simple linear backoff.
  useEffect(() => {
    if (!effectivelySignedIn) return
    if (!sourceId) return
    if (sourceLakehouseName && sourceWorkspaceName) return
    const sw = sourceWorkspaceOverride
    let attempt = 0
    let cancelled = false
    async function tick() {
      while (!cancelled && attempt < 6 && (!sourceLakehouseName || !sourceWorkspaceName)) {
        attempt++
        try {
          const { fabric } = await ensureToken()
          if (sw && !sourceWorkspaceName) {
            try {
              const r = await api<{ id: string; displayName: string | null }>(`/api/workspaces/${sw}`, fabric)
              if (!cancelled && r.displayName) setSourceWorkspaceName(r.displayName)
            } catch { /* ignore */ }
          }
          if (sourceId && !sourceLakehouseName) {
            try {
              const swQs = sw ? `?workspaceId=${sw}` : ''
              const r = await api<{ id: string; displayName: string }>(`/api/sources/lakehouses/${sourceId}${swQs}`, fabric)
              if (!cancelled && r.displayName) setSourceLakehouseName(r.displayName)
            } catch { /* ignore */ }
          }
        } catch { /* token failure — try again next tick */ }
        await new Promise(r => setTimeout(r, 1500 * attempt))
      }
    }
    tick()
    return () => { cancelled = true }
  }, [effectivelySignedIn, sourceId, sourceWorkspaceOverride, sourceLakehouseName, sourceWorkspaceName])

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

  // Unified poll: fetches both run status and token-usage on the same 4s tick.
  // - Usage continues polling 1 extra tick after the run reaches a terminal state to
  //   capture the final cost numbers.
  // - When the run is terminal (SpecsReady/Succeeded/Failed/Cancelled), the run-status
  //   poll stops; usage poll then also stops.
  useEffect(() => {
    if (!run) { setUsage(null); return }
    let cancelled = false
    async function tick() {
      try {
        const { fabric } = await ensureToken()
        const isTerminal = ['SpecsReady','Succeeded','Failed','Cancelled'].includes(run!.status)
        const fetches: Promise<unknown>[] = [
          api<any>(`/api/runs/${run!.runId}/usage`, fabric).then(u => { if (!cancelled) setUsage(u) }).catch(() => {})
        ]
        if (!isTerminal) {
          fetches.push(
            api<Run>(`/api/runs/${run!.runId}`, fabric).then(r => { if (!cancelled) setRun(r) }).catch(() => {})
          )
        }
        await Promise.all(fetches)
      } catch { /* token failure — retry next tick */ }
    }
    tick()
    const terminal = ['Succeeded','Failed','Cancelled'].includes(run.status)
    // Active runs: 4s. Terminal: do one final usage fetch above, no further polling.
    const id = terminal ? null : setInterval(tick, 4000)
    return () => { cancelled = true; if (id) clearInterval(id) }
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
        const resp = await api<{ error: string | null }>(`/api/runs/${run.runId}/error`, fabric, undefined, onelake)
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
  const buildStatusRef = useRef<HTMLDivElement | null>(null)
  const scrolledForRunRef = useRef<string | null>(null)

  // Once a build run is created, smoothly scroll the build-status card into view.
  // Only triggers once per runId so the user can scroll back up freely afterwards.
  useEffect(() => {
    if (!run?.runId) return
    if (scrolledForRunRef.current === run.runId) return
    if (!buildStatusRef.current) return
    scrolledForRunRef.current = run.runId
    // Defer to next frame so the card has actually rendered.
    requestAnimationFrame(() => {
      try { buildStatusRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }) }
      catch { /* old browsers */ }
    })
  }, [run?.runId])

  // Tracks the latest auto-propose "intent" so an in-flight call whose inputs are no
  // longer current can discard its result (e.g. user changed model dropdown mid-call).
  const proposeIntentRef = useRef<string | null>(null)

  async function previewSpecs() {
    if (!sourceId || selectedTables.size === 0) return
    const intent = `${selectedModel}|${sourceId}|${[...selectedTables].sort().join(',')}`
    proposeIntentRef.current = intent
    const modelAtCall = selectedModel
    setError(null); setBusy(true); setBusyMsg(`Reading source schemas and asking ${modelAtCall ?? 'AI'} to propose a spec...`)
    try {
      const { fabric, onelake } = await ensureToken()
      const resp = await api<{ markdown: string; runId: string; targetLakehouseName: string }>(
        '/api/specs/preview', fabric, {
          method: 'POST',
          body: JSON.stringify({
            sourceLakehouseId: sourceId,
            tables: Array.from(selectedTables),
            targetLakehouseName: targetName || null,
            model: modelAtCall
          })
        }, onelake)
      // Stale-result guard: if the user changed the model/source/tables while this call
      // was in flight, discard this response. The new effect tick will re-fire previewSpecs
      // with the now-current selection.
      if (proposeIntentRef.current !== intent) {
        console.log('[previewSpecs] discarding stale result; current intent diverged from', intent)
        return
      }
      setSpecDraft(resp.markdown)
      setPreviewRunId(resp.runId)
      if (!targetName) setTargetName(resp.targetLakehouseName)
    } catch (e: any) {
      if (proposeIntentRef.current === intent) setError(String(e))
    }
    finally {
      if (proposeIntentRef.current === intent) { setBusy(false); setBusyMsg('') }
    }
  }

  // Auto-propose: as soon as the user has chosen a model + source + at least one table,
  // call the LLM in the background and fill the spec editor. No "Propose Specs" click needed.
  useEffect(() => {
    if (!effectivelySignedIn) return
    if (!sourceId || selectedTables.size === 0) return
    if (!selectedModel) return
    if (run || specDraft) return
    const key = `${selectedModel}|${sourceId}|${[...selectedTables].sort().join(',')}`
    if (autoProposedForKeyRef.current === key) return
    // If an earlier auto-propose is still in flight with different inputs, mark our new
    // intent so its result is discarded; then fire a fresh call. The new call's setBusy/
    // setBusyMsg will overwrite the stale "asking gpt-5.4…" spinner.
    autoProposedForKeyRef.current = key
    previewSpecs().catch(e => console.warn('[auto-propose] failed', e))
  }, [effectivelySignedIn, sourceId, selectedTables, selectedModel, run, specDraft])

  async function saveAndBuild() {
    if (!sourceId || selectedTables.size === 0) return
    setError(null); setBusy(true); setBusyMsg('Saving spec to lakehouse Files/spec.md…')
    // Fresh build → reset iteration tracking so auto-fix gets a full budget.
    setCurrentIteration(1)
    triedAutoFixForKey.current.clear()
    lastErrorSignatureRef.current = null
    sameSignatureCountRef.current = 0
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
    sameSignatureCountRef.current = 0
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

  // After the user edits a section, ask the LLM to revise downstream sections so the
  // chain (Bronze → Silver → Gold → Semantic → Report → Data Agent) stays consistent.
  async function propagateDownstream(editedSectionKey: string) {
    if (!['bronze','silver','gold','semantic','report'].includes(editedSectionKey)) return
    setError(null); setBusy(true)
    const labelMap: Record<string,string> = { bronze: 'Silver/Gold/Semantic/Report/Data Agent', silver: 'Gold/Semantic/Report/Data Agent', gold: 'Semantic/Report/Data Agent', semantic: 'Report/Data Agent', report: 'Data Agent' }
    setBusyMsg(`Asking ${selectedModel ?? 'AI'} to update ${labelMap[editedSectionKey] ?? 'downstream sections'}…`)
    try {
      const combined = recombineSpec()
      const { fabric } = await ensureToken()
      const r = await api<{ markdown: string }>('/api/specs/propagate', fabric, {
        method: 'POST',
        body: JSON.stringify({ currentSpec: combined, editedSection: editedSectionKey, model: selectedModel })
      })
      if (r.markdown) setSpecDraft(r.markdown)
    } catch (e: any) { setError(`Propagate downstream failed: ${String(e)}`) }
    finally { setBusy(false); setBusyMsg('') }
  }

  async function fixSpec() {
    if (!run || !effectiveErrorTrace) return
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
        body: JSON.stringify({ currentSpec: combined, errorTrace: effectiveErrorTrace, model: selectedModel, iteration: currentIteration, failedLayer: run.currentLayer })
      })
      if (r.markdown) {
        setSpecDraft(r.markdown)
        setTimeout(() => {
          // Open the gold section by default since most LLM fixes target it
          setOpenSection('gold')
        }, 100)
      }
    } catch (e: any) { setError(String(e)) }
    finally { setBusy(false); setBusyMsg('') }
  }

  // Full auto-fix-and-rebuild cycle: ask LLM to revise spec, push it, rebuild.
  // Returns true on success (build kicked off), false on failure.
  async function autoFixAndRebuild(): Promise<boolean> {
    if (!run || !effectiveErrorTrace) return false
    const combined = recombineSpec()
    if (!combined) return false
    const { fabric } = await ensureToken()
    setBusyMsg(`Iteration ${currentIteration + 1}/${maxIterations}: revising spec...`)
    const fixResp = await api<{ markdown: string }>(`/api/runs/${run.runId}/fix-spec`, fabric, {
      method: 'POST',
      body: JSON.stringify({ currentSpec: combined, errorTrace: effectiveErrorTrace, model: selectedModel, iteration: currentIteration, failedLayer: run.currentLayer })
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
  // Effective error trace for the LLM: prefer the detailed Spark traceback (written by
  // _save_error to lakehouse Files/error.txt), but fall back to run.message when the
  // Spark session crashed before any cell could write the traceback (e.g.
  // "System_Cancelled_Session_Statements_Failed"). Without this fallback the auto-fix
  // useEffect would short-circuit on `!sparkError` and never iterate.
  const effectiveErrorTrace = sparkError || (run?.message ?? null)

  useEffect(() => {
    if (!run || !effectiveErrorTrace) return
    if (run.status !== 'Failed' && run.status !== 'Cancelled') return
    if (currentIteration >= maxIterations) return
    if (autoFixing) return
    if (stuckOnSameError) return
    const key = `${run.runId}::iter${currentIteration}`
    if (triedAutoFixForKey.current.has(key)) return

    const signature = signatureFromTrace(effectiveErrorTrace ?? '')
    if (lastErrorSignatureRef.current === signature) {
      sameSignatureCountRef.current += 1
    } else {
      sameSignatureCountRef.current = 1
    }
    // Only declare "stuck" after 3 consecutive identical signatures — gives the LLM
    // a few real chances even when its first fix attempt didn't move the needle.
    if (sameSignatureCountRef.current >= 3) {
      setStuckOnSameError(true)
      return
    }
    lastErrorSignatureRef.current = signature

    setAutoFixing(true)
    setError(null); setBusy(true)
    autoFixAndRebuild()
      .then(ok => {
        // Only mark this iteration as "consumed" once it actually succeeded; if it returned
        // false or threw, leave the key out so we can retry.
        if (ok) {
          triedAutoFixForKey.current.add(key)
        } else {
          // Returned false (e.g., recombineSpec was empty). Schedule a retry in 3s.
          setTimeout(() => setAutoFixRetryTick(t => t + 1), 3000)
        }
      })
      .catch(e => {
        console.error('[autoFix] failed', e)
        setError(`Auto-fix failed (will retry in 5s): ${String(e)}`)
        // Schedule a retry so the loop is not permanently stuck on a transient error.
        setTimeout(() => setAutoFixRetryTick(t => t + 1), 5000)
      })
      .finally(() => { setAutoFixing(false); setBusy(false); setBusyMsg('') })
  }, [run?.runId, run?.status, effectiveErrorTrace, currentIteration, maxIterations, stuckOnSameError, autoFixRetryTick])

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
        <a href="#" onClick={(e) => e.preventDefault()} title="Copilot Medallion">
          <img src="/logo.png" alt="Copilot Medallion" className={s.logoLeft} />
        </a>
        <div className={s.headerTitle}>
          <Title1 block>🛠️ Copilot Medallion</Title1>
          <Body1 block style={{ marginTop: 2 }}>Automated Bronze → Silver → Gold &amp; semantic model &amp; report &amp; data agent creator</Body1>
          <Caption1 block style={{ marginTop: 6 }}>
            {itemWorkspaceName ? <>Workspace: <strong>{itemWorkspaceName}</strong></> : (fabricWorkspaceId ? <>Workspace: <code>{fabricWorkspaceId}</code></> : null)}
            {signedIn && <> · <FLink onClick={signOut} as="button" style={{ background: 'none', border: 'none', cursor: 'pointer' }}>Sign out</FLink></>}
            {effectivelySignedIn && <> · <FLink onClick={() => { setGuidanceOpen(true); if (!guidanceItems) loadGuidance() }} as="button" style={{ background: 'none', border: 'none', cursor: 'pointer' }}>📚 Guidance history</FLink></>}
          </Caption1>
        </div>
        <a href="https://github.com/Remc0000" target="_blank" rel="noreferrer" title="Remc0000 on GitHub">
          <img src="/remc0000-wordmark.png" alt="Remc0000" className={s.logo} />
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
                <div>Source Workspace: <strong>{sourceWorkspaceName ?? '(loading…)'}</strong></div>
                <div>Source Lakehouse: <strong>{sourceLakehouseName ?? '(loading…)'}</strong></div>
                <div>
                  <strong>{selectedTables.size}</strong> table{selectedTables.size === 1 ? '' : 's'} selected:
                </div>
                {selectedTables.size > 0 && (
                  <div style={{ marginTop: 6, display: 'flex', flexWrap: 'wrap', gap: '4px 8px', maxHeight: 220, overflow: 'auto', padding: '6px 8px', background: tokens.colorNeutralBackground3, borderRadius: 4 }}>
                    {Array.from(selectedTables).sort().map(t => (
                      <code key={t} style={{ fontSize: 12, background: tokens.colorNeutralBackground1, padding: '2px 6px', borderRadius: 3 }}>{t}</code>
                    ))}
                  </div>
                )}
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
                        {['bronze','silver','gold','semantic','report'].includes(sec.key) && (
                          <div style={{ marginTop: 8, display: 'flex', alignItems: 'center', gap: 10 }}>
                            <Button
                              size="small"
                              disabled={busy}
                              onClick={() => propagateDownstream(sec.key)}
                              icon={<span>🔄</span>}
                            >
                              Update downstream sections
                            </Button>
                            <Caption1>Re-runs the LLM to keep {sec.key === 'bronze' ? 'Silver/Gold/Semantic/Report/Data Agent' : sec.key === 'silver' ? 'Gold/Semantic/Report/Data Agent' : sec.key === 'gold' ? 'Semantic/Report/Data Agent' : sec.key === 'semantic' ? 'Report/Data Agent' : 'Data Agent'} consistent with your edits above.</Caption1>
                          </div>
                        )}
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
        <Card ref={buildStatusRef}>
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
                  {usage && usage.totalTokens > 0 && (
                    <> · 🪙 <strong>{usage.totalTokens.toLocaleString()}</strong> tokens
                      {' '}<Caption1 as="span">({usage.promptTokens.toLocaleString()} in / {usage.completionTokens.toLocaleString()} out · {usage.requests} call{usage.requests === 1 ? '' : 's'})</Caption1>
                      {usage.estimatedCostUsd > 0 && <> · ~<strong>${usage.estimatedCostUsd.toFixed(4)}</strong></>}
                    </>
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
                <div key={st.key} className={cls} style={st.indent ? { marginLeft: 28, borderLeft: `2px solid ${tokens.colorNeutralStroke2}`, paddingLeft: 12 } : undefined}>
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
                  <Button appearance="primary" as="a" href={`https://app.fabric.microsoft.com/groups/${run.workspaceId}/lakehouses/${run.targetLakehouseId}`} target="_blank" rel="noopener noreferrer" onClick={() => { if (window.parent !== window) openExternal(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/lakehouses/${run.targetLakehouseId}`) }}>
                    Open Lakehouse
                  </Button>
                )}
                {run.bronzeNotebookId && run.workspaceId && (
                  <Button as="a" href={`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${run.bronzeNotebookId}`} target="_blank" rel="noopener noreferrer" onClick={() => { if (window.parent !== window) openExternal(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${run.bronzeNotebookId}`) }}>
                    Open Bronze
                  </Button>
                )}
                {run.silverNotebookId && run.workspaceId && (
                  <Button as="a" href={`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${run.silverNotebookId}`} target="_blank" rel="noopener noreferrer" onClick={() => { if (window.parent !== window) openExternal(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${run.silverNotebookId}`) }}>
                    Open Silver
                  </Button>
                )}
                {run.goldNotebookId && run.workspaceId && (
                  <Button as="a" href={`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${run.goldNotebookId}`} target="_blank" rel="noopener noreferrer" onClick={() => { if (window.parent !== window) openExternal(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${run.goldNotebookId}`) }}>
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
                  {effectiveErrorTrace && (
                    <details style={{ marginTop: 8 }} open>
                      <summary style={{ cursor: 'pointer' }}>{sparkError ? 'Spark cell traceback' : 'Failure message'}</summary>
                      <pre style={{ fontSize: 11, marginTop: 6, maxHeight: 320, overflow: 'auto', whiteSpace: 'pre-wrap', background: tokens.colorNeutralBackground3, padding: 8, borderRadius: 4 }}>{effectiveErrorTrace}</pre>
                    </details>
                  )}
                </MessageBarBody>
              </MessageBar>
              <div className={s.row}>
                {effectiveErrorTrace && !autoFixing && (stuckOnSameError || currentIteration >= maxIterations) && (
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
                    <Button as="a" href={`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${layerNb}`} target="_blank" rel="noopener noreferrer" onClick={() => { if (window.parent !== window) openExternal(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/synapsenotebooks/${layerNb}`) }}>
                      Open failed {run.currentLayer ?? ''} notebook
                    </Button>
                  )
                })()}
                {run.workspaceId && (
                  <Button as="a" href={`https://app.fabric.microsoft.com/groups/${run.workspaceId}/list?experience=power-bi`} target="_blank" rel="noopener noreferrer" onClick={() => { if (window.parent !== window) openExternal(`https://app.fabric.microsoft.com/groups/${run.workspaceId}/list?experience=power-bi`) }}>
                    Open Workspace
                  </Button>
                )}
                <Button as="a" href={'https://app.fabric.microsoft.com/monitoringhub?experience=power-bi'} target="_blank" rel="noopener noreferrer" onClick={() => { if (window.parent !== window) openExternal('https://app.fabric.microsoft.com/monitoringhub?experience=power-bi') }}>
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
