import type { MessageKey } from './i18n'

export function seasonLabel(
  t: (key: MessageKey, params?: Record<string, string | number>) => string,
  number: number,
) {
  return number === 0
    ? t('details.specials')
    : t('details.seasonName', { number })
}
