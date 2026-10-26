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

export async function restoreBackup(file: File): Promise<void> {
  const body = new FormData()
  body.append('file', file)
  await request<{ restorePending: boolean }>(
    'POST',
    `${backupsPath}/restore`,
    body,
  )
}
