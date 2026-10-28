export class ApiError extends Error {
  status: number
  retryAfter: number | null

  constructor(
    status: number,
    message: string,
    retryAfter: number | null = null,
  ) {
    super(message)
    this.status = status
    this.retryAfter = retryAfter
  }
}

type Antiforgery = { token: string; headerName: string }

let antiforgery: Antiforgery | null = null

export function resetAntiforgery() {
  antiforgery = null
}

async function getAntiforgery(): Promise<Antiforgery> {
  if (!antiforgery) {
    const response = await fetch('/api/v1/auth/antiforgery')
    antiforgery = (await response.json()) as Antiforgery
  }
  return antiforgery
}

export async function requestWithStatus<T>(
  method: string,
  path: string,
  body?: unknown,
  signal?: AbortSignal,
): Promise<{ status: number; body: T }> {
  const headers: Record<string, string> = {}
  const isForm = body instanceof FormData
  if (body !== undefined && !isForm) {
    headers['Content-Type'] = 'application/json'
  }
  if (method !== 'GET') {
    const tokens = await getAntiforgery()
    headers[tokens.headerName] = tokens.token
  }
  const response = await fetch(path, {
    method,
    headers,
    body: isForm ? body : body === undefined ? undefined : JSON.stringify(body),
    signal,
  })
  if (!response.ok) {
    let message = `Request failed (${response.status})`
    try {
      const problem = (await response.json()) as { title?: string }
      if (problem.title) {
        message = problem.title
      }
    } catch {
      // The response had no problem body.
    }
    const retryAfter = Number.parseInt(
      response.headers.get('Retry-After') ?? '',
      10,
    )
    throw new ApiError(
      response.status,
      message,
      Number.isNaN(retryAfter) ? null : retryAfter,
    )
  }
  if (response.status === 204) {
    return { status: response.status, body: undefined as T }
  }
  return { status: response.status, body: (await response.json()) as T }
}

export async function request<T>(
  method: string,
  path: string,
  body?: unknown,
  signal?: AbortSignal,
): Promise<T> {
  return (await requestWithStatus<T>(method, path, body, signal)).body
}

export const maxParentNoteLength = 2000

export type ParentNoteResponse = {
  kind: 'movie' | 'show'
  tmdbId: number
  note: string | null
}

function readParentNote(body: unknown): ParentNoteResponse {
  if (typeof body !== 'object' || body === null) {
    throw new ApiError(200, 'Request failed (200)')
  }
  const value = body as Record<string, unknown>
  if (
    (value.kind !== 'movie' && value.kind !== 'show') ||
    typeof value.tmdbId !== 'number' ||
    !(value.note === null || typeof value.note === 'string')
  ) {
    throw new ApiError(200, 'Request failed (200)')
  }
  return { kind: value.kind, tmdbId: value.tmdbId, note: value.note }
}

export async function getParentNote(kind: 'movie' | 'show', tmdbId: number) {
  return readParentNote(
    await request<unknown>('GET', `/api/v1/titles/${kind}/${tmdbId}/note`),
  )
}

export async function saveParentNote(
  kind: 'movie' | 'show',
  tmdbId: number,
  note: string,
) {
  return readParentNote(
    await request<unknown>('PUT', `/api/v1/titles/${kind}/${tmdbId}/note`, {
      note,
    }),
  )
}
