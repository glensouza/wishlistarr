export type NoticePackage = {
  name: string
  version: string
  license: string
}

function text(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  return trimmed === '' ? null : trimmed
}

export function readNotices(data: unknown): NoticePackage[] | null {
  if (data === null || typeof data !== 'object' || Array.isArray(data))
    return null
  const packages = (data as { packages?: unknown }).packages
  if (!Array.isArray(packages) || packages.length === 0) return null
  const notices: NoticePackage[] = []
  for (const entry of packages) {
    if (entry === null || typeof entry !== 'object' || Array.isArray(entry)) {
      return null
    }
    const record = entry as {
      name?: unknown
      version?: unknown
      license?: unknown
    }
    const name = text(record.name)
    const version = text(record.version)
    const license = text(record.license)
    if (!name || !version || !license) return null
    notices.push({ name, version, license })
  }
  return notices
}
