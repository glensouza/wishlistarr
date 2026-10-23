import { request } from './api'

export type BackupItem = {
  name: string
  createdAt: string
  sizeBytes: number
}

export type BackupList = {
  retention: number
  restorePending: boolean
  backups: BackupItem[]
}

export type BackupRun = {
  id: number
  outcome: string
}

const backupsPath = '/api/v1/backups'
export const backupJobName = 'database-backup'

export const listBackups = () => request<BackupList>('GET', backupsPath)

export const downloadBackupPath = (name: string) =>
  `${backupsPath}/${encodeURIComponent(name)}/download`

export const startBackup = () =>
  request<{ runId: number }>('POST', `/api/v1/jobs/${backupJobName}/run`)

export const latestBackupRun = async (runId: number) => {
  const runs = await request<BackupRun[]>(
    'GET',
    `/api/v1/jobs/${backupJobName}/runs?limit=5`,
  )
  return runs.find((run) => run.id === runId) ?? null
}

async function antiforgeryHeader(): Promise<Record<string, string>> {
  const response = await fetch('/api/v1/auth/antiforgery')
  const tokens = (await response.json()) as {
    token: string
    headerName: string
  }
  return { [tokens.headerName]: tokens.token }
}

export async function restoreBackup(file: File): Promise<void> {
  const headers = await antiforgeryHeader()
  const body = new FormData()
  body.append('file', file)
  const response = await fetch(`${backupsPath}/restore`, {
    method: 'POST',
    headers,
    body,
  })
  if (!response.ok) {
    let message = `Request failed (${response.status})`
    try {
      const problem = (await response.json()) as { title?: string }
      if (problem.title) message = problem.title
    } catch {
      // The response had no problem body.
    }
    throw new Error(message)
  }
}
