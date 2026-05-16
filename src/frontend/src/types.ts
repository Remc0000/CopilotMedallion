export interface AppConfig {
  tenantId: string
  clientId: string
  workspaceId: string
  scope: string
  runsRepo: string
}

export interface Lakehouse { id: string; displayName: string; workspaceId: string; description?: string }
export interface Table { name: string; path?: string }

export interface SpecResponse { runId: string; branch: string; specUrl: string; specRawUrl: string }

export interface Run {
  runId: string
  status: string
  branch?: string
  specUrl?: string
  workspaceId?: string
  sourceWorkspaceId?: string
  sourceLakehouseId?: string
  tablesCsv?: string
  targetLakehouseId?: string
  targetLakehouseName?: string
  notebookId?: string
  jobInstanceId?: string
  message?: string
  createdAt: string
  updatedAt: string
  bronzeNotebookId?: string
  silverNotebookId?: string
  goldNotebookId?: string
  reportingNotebookId?: string
  bronzeJobId?: string
  silverJobId?: string
  goldJobId?: string
  reportingJobId?: string
  currentLayer?: string
}
