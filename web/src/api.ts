export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
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

export async function request<T>(
  method: string,
  path: string,
  body?: unknown,
): Promise<T> {
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
    throw new ApiError(response.status, message)
  }
  if (response.status === 204) {
    return undefined as T
  }
  return (await response.json()) as T
}
