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
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }
  if (method !== 'GET') {
    const tokens = await getAntiforgery()
    headers[tokens.headerName] = tokens.token
  }
  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
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
