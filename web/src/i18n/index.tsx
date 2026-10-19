import { createContext, useContext, useMemo, type ReactNode } from 'react'
import { en, type MessageKey } from './en'

export type { MessageKey }

type Params = Record<string, string | number>
type Translate = (key: MessageKey, params?: Params) => string

const catalogs: Record<string, Record<MessageKey, string>> = { en }

export const defaultLocale = 'en'

export function createTranslator(locale: string): Translate {
  const catalog = catalogs[locale] ?? catalogs[defaultLocale]
  return (key, params) =>
    catalog[key].replace(/\{(\w+)\}/g, (match, name: string) =>
      name in (params ?? {}) ? String(params?.[name]) : match,
    )
}

const I18nContext = createContext<Translate>(createTranslator(defaultLocale))

export function I18nProvider({
  locale = defaultLocale,
  children,
}: {
  locale?: string
  children: ReactNode
}) {
  const translate = useMemo(() => createTranslator(locale), [locale])
  return (
    <I18nContext.Provider value={translate}>{children}</I18nContext.Provider>
  )
}

export const useT = () => useContext(I18nContext)
