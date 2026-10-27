import { ApiError } from './api'
import type { MessageKey } from './i18n'

export function contentControlError(
  error: unknown,
  importing = false,
): MessageKey {
  if (!(error instanceof ApiError)) return 'controls.networkError'
  if (error.status === 409) return 'controls.duplicateTitle'
  if (error.status === 404) return 'controls.notFound'
  if (error.status >= 500) return 'controls.serverError'
  return importing ? 'controls.importInvalid' : 'controls.titleFailed'
}
