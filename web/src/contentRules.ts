import { ratingLevels, type KidProfile } from './kidProfiles'

export const maxAdvisoryEntries = 50
export const maxAdvisoryLength = 100

export const ruleKeys = [
  'movieAgeLimit',
  'tvAgeLimit',
  'weeklyAllowance',
  'autoApprove',
  'autoApproveMovieRating',
  'autoApproveTvRating',
  'blockedGenreIds',
  'blockedKeywordIds',
  'allowedOriginalLanguages',
  'blockedDescriptors',
  'blockedTopics',
] as const

export type ProfileRules = Pick<KidProfile, (typeof ruleKeys)[number]>

export function profileRules(profile: ProfileRules): ProfileRules {
  return Object.fromEntries(
    ruleKeys.map((key) => [key, profile[key]]),
  ) as ProfileRules
}

export function validRules(value: unknown): value is ProfileRules {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  const rules = value as Record<string, unknown>
  if (Object.keys(rules).length !== ruleKeys.length) return false
  const integer = (item: unknown, max: number) =>
    item === null ||
    (typeof item === 'number' &&
      Number.isInteger(item) &&
      item >= 0 &&
      item <= max)
  const ids = (item: unknown, max: number) =>
    Array.isArray(item) &&
    item.length <= max &&
    item.every(
      (id: unknown) =>
        typeof id === 'number' &&
        Number.isInteger(id) &&
        id > 0 &&
        id <= 2147483647,
    )
  const rating = (item: unknown) =>
    item === null || ratingLevels.some((level) => level === item)
  const advisories = (item: unknown) =>
    Array.isArray(item) &&
    item.length <= maxAdvisoryEntries &&
    item.every(
      (entry: unknown) =>
        typeof entry === 'string' &&
        entry.length >= 1 &&
        entry.length <= maxAdvisoryLength &&
        !Array.from(entry).some(
          (character) =>
            character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127,
        ),
    )
  return (
    integer(rules.movieAgeLimit, 21) &&
    integer(rules.tvAgeLimit, 21) &&
    integer(rules.weeklyAllowance, 1000) &&
    typeof rules.autoApprove === 'boolean' &&
    rating(rules.autoApproveMovieRating) &&
    rating(rules.autoApproveTvRating) &&
    ids(rules.blockedGenreIds, 40) &&
    ids(rules.blockedKeywordIds, 100) &&
    Array.isArray(rules.allowedOriginalLanguages) &&
    rules.allowedOriginalLanguages.length <= 50 &&
    rules.allowedOriginalLanguages.every(
      (code: unknown) => typeof code === 'string' && /^[a-z]{2}$/i.test(code),
    ) &&
    advisories(rules.blockedDescriptors) &&
    advisories(rules.blockedTopics)
  )
}

export type TitleRule = {
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  blocked: boolean
}
export function validTitles(value: unknown): value is TitleRule[] {
  if (!Array.isArray(value) || value.length > 1000) return false
  const keys = new Set<string>()
  return value.every((row: unknown) => {
    if (!row || typeof row !== 'object') return false
    const item = row as TitleRule
    const key = `${item.kind}:${item.tmdbId}:${item.blocked}`
    if (keys.has(key)) return false
    keys.add(key)
    return (
      Object.keys(item).sort().join(',') === 'blocked,kind,title,tmdbId' &&
      (item.kind === 'movie' || item.kind === 'show') &&
      Number.isInteger(item.tmdbId) &&
      item.tmdbId > 0 &&
      item.tmdbId <= 2147483647 &&
      typeof item.title === 'string' &&
      item.title.trim().length > 0 &&
      item.title.length <= 300 &&
      !Array.from(item.title).some(
        (character) =>
          character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127,
      ) &&
      typeof item.blocked === 'boolean'
    )
  })
}
export function exportRules(
  rules: ProfileRules,
  titles: TitleRule[] = [],
): string {
  return JSON.stringify(
    { version: 1, rules: profileRules(rules), titles },
    null,
    2,
  )
}

export function importRules(text: string): ProfileRules | null {
  try {
    const data: unknown = JSON.parse(text)
    if (!data || typeof data !== 'object' || Array.isArray(data)) return null
    const envelope = data as Record<string, unknown>
    if (
      Object.keys(envelope).length !== 3 ||
      envelope.version !== 1 ||
      !validRules(envelope.rules) ||
      !validTitles(envelope.titles)
    )
      return null
    return envelope.rules
  } catch {
    return null
  }
}
