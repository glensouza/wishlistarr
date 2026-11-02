import { ApiError } from '../api'

export function setupError(failure: unknown, fallback: string) {
  return failure instanceof ApiError &&
    [400, 404, 409, 502].includes(failure.status)
    ? failure.message
    : fallback
}
