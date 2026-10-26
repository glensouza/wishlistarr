import { useEffect, useState, type FormEvent } from 'react'
import {
  avatars,
  builtInHomeRows,
  createKidProfile,
  deleteKidProfile,
  getHomeOptions,
  listKidProfiles,
  ratingLevels,
  unlockKidProfile,
  updateKidProfile,
  type HomeOptions,
  type KidProfile,
  type KidProfileInput,
  type PlexRestrictions,
  type RatingLevel,
} from './kidProfiles'
import { useT, type MessageKey } from './i18n'
import PlexUserPicker from './PlexUserPicker'

type FormState = {
  name: string
  avatar: string
  pin: string
  removePin: boolean
  movieAgeLimit: string
  tvAgeLimit: string
  plexLabel: string
  plexUserId: string
  weeklyAllowance: string
  autoApprove: boolean
  movieCeiling: string
  tvCeiling: string
  blockedGenres: string
  blockedKeywords: string
  allowedLanguages: string
  homeRows: HomeChoice[]
}

type HomeChoice = { id: string; enabled: boolean }

function withDefaults(rows: HomeChoice[], defaults: string[]): HomeChoice[] {
  const choices = rows.slice()
  for (const id of defaults) {
    if (!rows.some((choice) => choice.id === id)) {
      choices.push({ id, enabled: false })
    }
  }
  return choices
}

const homeChoices = (configured: string[], defaults: string[]) =>
  withDefaults(
    configured.map((id) => ({ id, enabled: true })),
    defaults,
  )

const ratingKey: Record<RatingLevel, MessageKey> = {
  preschool: 'rating.preschool',
  general: 'rating.general',
  children: 'rating.children',
  parentalGuidance: 'rating.parentalGuidance',
  teen: 'rating.teen',
  mature: 'rating.mature',
  adult: 'rating.adult',
}

const emptyForm = (defaults: string[]): FormState => ({
  name: '',
  avatar: avatars[0],
  pin: '',
  removePin: false,
  movieAgeLimit: '',
  tvAgeLimit: '',
  plexLabel: '',
  plexUserId: '',
  weeklyAllowance: '',
  autoApprove: false,
  movieCeiling: '',
  tvCeiling: '',
  blockedGenres: '',
  blockedKeywords: '',
  allowedLanguages: '',
  homeRows: homeChoices(defaults, defaults),
})

function toForm(profile: KidProfile, defaults: string[]): FormState {
  return {
    name: profile.name,
    avatar: profile.avatar,
    pin: '',
    removePin: false,
    movieAgeLimit: profile.movieAgeLimit?.toString() ?? '',
    tvAgeLimit: profile.tvAgeLimit?.toString() ?? '',
    plexLabel: profile.plexLabel,
    plexUserId: profile.plexUserId ?? '',
    weeklyAllowance: profile.weeklyAllowance?.toString() ?? '',
    autoApprove: profile.autoApprove,
    movieCeiling: profile.autoApproveMovieRating ?? '',
    tvCeiling: profile.autoApproveTvRating ?? '',
    blockedGenres: profile.blockedGenreIds.join(', '),
    blockedKeywords: profile.blockedKeywordIds.join(', '),
    allowedLanguages: profile.allowedOriginalLanguages.join(', '),
    homeRows: homeChoices(profile.homeRows, defaults),
  }
}

function parseTokens(value: string): string[] {
  return value
    .split(',')
    .map((part) => part.trim())
    .filter((part) => part !== '')
}

function parseIds(value: string): number[] | null {
  const ids: number[] = []
  for (const token of parseTokens(value)) {
    if (!/^[1-9]\d*$/.test(token)) return null
    ids.push(Number(token))
  }
  return ids
}

const toNumber = (value: string) => (value.trim() === '' ? null : Number(value))

function ceiling(value: string): RatingLevel | null {
  return ratingLevels.find((level) => level === value) ?? null
}

function toInput(form: FormState): KidProfileInput | null {
  const blockedGenreIds = parseIds(form.blockedGenres)
  const blockedKeywordIds = parseIds(form.blockedKeywords)
  if (blockedGenreIds === null || blockedKeywordIds === null) return null
  return {
    name: form.name,
    avatar: form.avatar,
    pin: form.pin === '' ? undefined : form.pin,
    removePin: form.removePin,
    movieAgeLimit: toNumber(form.movieAgeLimit),
    tvAgeLimit: toNumber(form.tvAgeLimit),
    plexLabel: form.plexLabel,
    plexUserId: form.plexUserId === '' ? null : form.plexUserId,
    weeklyAllowance: toNumber(form.weeklyAllowance),
    autoApprove: form.autoApprove,
    autoApproveMovieRating: ceiling(form.movieCeiling),
    autoApproveTvRating: ceiling(form.tvCeiling),
    blockedGenreIds,
    blockedKeywordIds,
    allowedOriginalLanguages: parseTokens(form.allowedLanguages),
    homeRows: form.homeRows
      .filter((choice) => choice.enabled)
      .map((choice) => choice.id),
  }
}

const messageOf = (failure: unknown, fallback: string) =>
  failure instanceof Error ? failure.message : fallback

export default function KidProfilesAdmin() {
  const t = useT()
  const [profiles, setProfiles] = useState<KidProfile[]>([])
  const [editing, setEditing] = useState<KidProfile | 'new' | null>(null)
  const [deleting, setDeleting] = useState<KidProfile | null>(null)
  const [options, setOptions] = useState<HomeOptions | null>(null)
  const [optionsFailed, setOptionsFailed] = useState(false)
  const [optionsVersion, setOptionsVersion] = useState(0)
  const [error, setError] = useState<string | null>(null)

  const [version, setVersion] = useState(0)
  const load = () => setVersion((current) => current + 1)

  useEffect(() => {
    let current = true
    listKidProfiles().then(
      (loaded) => current && setProfiles(loaded),
      (failure: unknown) =>
        current && setError(messageOf(failure, t('app.somethingWrong'))),
    )
    return () => {
      current = false
    }
  }, [version, t])

  useEffect(() => {
    let current = true
    getHomeOptions().then(
      (loaded) => {
        if (!current) return
        setOptions(loaded)
        setOptionsFailed(false)
      },
      () => current && setOptionsFailed(true),
    )
    return () => {
      current = false
    }
  }, [optionsVersion])

  function retryOptions() {
    setOptionsFailed(false)
    setOptionsVersion((current) => current + 1)
  }

  async function unlock(profile: KidProfile) {
    setError(null)
    try {
      await unlockKidProfile(profile.id)
      load()
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    }
  }

  return (
    <section aria-labelledby="kid-profiles-heading">
      <h2 id="kid-profiles-heading">{t('admin.title')}</h2>
      {error && !editing && <p role="alert">{error}</p>}
      <ul>
        {profiles.map((profile) => (
          <li key={profile.id}>
            <strong>{profile.name}</strong> ({profile.avatar})
            {profile.isLocked && <span> {t('admin.locked')}</span>}
            <RestrictionNotes restrictions={profile.plexRestrictions} />
            <button type="button" onClick={() => setEditing(profile)}>
              {t('admin.edit', { name: profile.name })}
            </button>
            {profile.isLocked && (
              <button type="button" onClick={() => void unlock(profile)}>
                {t('admin.unlock', { name: profile.name })}
              </button>
            )}
            <button type="button" onClick={() => setDeleting(profile)}>
              {t('admin.delete', { name: profile.name })}
            </button>
          </li>
        ))}
      </ul>
      {profiles.length === 0 && <p>{t('admin.none')}</p>}
      {editing === null && (
        <button type="button" onClick={() => setEditing('new')}>
          {t('admin.add')}
        </button>
      )}
      {editing !== null && (
        <ProfileForm
          options={options}
          optionsFailed={optionsFailed}
          onRetryOptions={retryOptions}
          key={editing === 'new' ? 'new' : editing.id}
          profile={editing === 'new' ? null : editing}
          onCancel={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            load()
          }}
        />
      )}
      {deleting && (
        <DeleteConfirm
          profile={deleting}
          onCancel={() => setDeleting(null)}
          onDeleted={() => {
            setDeleting(null)
            load()
          }}
        />
      )}
    </section>
  )
}

function RestrictionNotes({
  restrictions,
}: {
  restrictions?: PlexRestrictions | null
}) {
  const t = useT()
  if (!restrictions) return null
  const kinds = [
    ['admin.restriction.movies', restrictions.movies],
    ['admin.restriction.television', restrictions.television],
  ] as const
  return (
    <ul aria-label={t('admin.restriction.heading')}>
      {kinds.map(([key, restriction]) => {
        const type = t(key)
        const message =
          restriction.state === 'notEnforced'
            ? t('admin.restriction.notEnforced', { type })
            : restriction.state === 'unknown'
              ? t('admin.restriction.unknown', { type })
              : t('admin.restriction.enforced', { type })
        return <li key={key}>{message}</li>
      })}
    </ul>
  )
}

type ProfileFormProps = {
  options: HomeOptions | null
  optionsFailed: boolean
  onRetryOptions: () => void
  profile: KidProfile | null
  onCancel: () => void
  onSaved: () => void
}

function ProfileForm({
  options,
  optionsFailed,
  onRetryOptions,
  profile,
  onCancel,
  onSaved,
}: ProfileFormProps) {
  const t = useT()
  const defaults = options?.defaultRows ?? builtInHomeRows
  const genres = options?.genres ?? []
  const [form, setForm] = useState<FormState>(
    profile ? toForm(profile, defaults) : emptyForm(defaults),
  )
  const [error, setError] = useState<string | null>(null)
  const homeRows = withDefaults(form.homeRows, defaults)

  const set = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setForm((current) => ({ ...current, [key]: value }))

  function moveHomeRow(index: number, delta: number) {
    const next = homeRows.slice()
    const target = index + delta
    if (target < 0 || target >= next.length) return
    const [moved] = next.splice(index, 1)
    next.splice(target, 0, moved)
    set('homeRows', next)
  }

  function homeRowName(id: string, translate: typeof t) {
    if (id === 'popular') return translate('admin.rowPopular')
    if (id === 'library') return translate('admin.rowLibrary')
    if (id === 'genre') return translate('admin.rowGenre')
    const genre = /^genre:(\d+)$/.exec(id)
    if (!genre) return id
    const known = genres.find((item) => item.id === Number(genre[1]))
    return known ? known.name : translate('admin.genreRow', { id: genre[1] })
  }

  const pickable = genres.filter(
    (genre) => !homeRows.some((choice) => choice.id === `genre:${genre.id}`),
  )

  function addGenreRow(value: string) {
    if (value === '') return
    set('homeRows', [...homeRows, { id: `genre:${value}`, enabled: true }])
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    if (options === null) return
    try {
      const input = toInput({ ...form, homeRows })
      if (input === null) {
        setError(t('admin.rulesInvalid'))
        return
      }
      if (profile) {
        await updateKidProfile(profile.id, input)
      } else {
        await createKidProfile(input)
      }
      onSaved()
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    }
  }

  return (
    <form
      onSubmit={submit}
      aria-label={profile ? t('admin.editForm') : t('admin.newForm')}
    >
      <label>
        {t('admin.name')}
        <input
          value={form.name}
          onChange={(event) => set('name', event.target.value)}
        />
      </label>
      <label>
        {t('admin.avatar')}
        <select
          value={form.avatar}
          onChange={(event) => set('avatar', event.target.value)}
        >
          {avatars.map((avatar) => (
            <option key={avatar} value={avatar}>
              {avatar}
            </option>
          ))}
        </select>
      </label>
      <label>
        {t('admin.pin')}
        <input
          type="password"
          inputMode="numeric"
          autoComplete="off"
          value={form.pin}
          placeholder={
            profile?.hasPin ? t('admin.pinUnchanged') : t('admin.pinHint')
          }
          onChange={(event) => set('pin', event.target.value)}
        />
      </label>
      {profile?.hasPin && (
        <label>
          <input
            type="checkbox"
            checked={form.removePin}
            onChange={(event) => set('removePin', event.target.checked)}
          />
          {t('admin.removePin')}
        </label>
      )}
      <label>
        {t('admin.movieLimit')}
        <input
          type="number"
          min={0}
          max={21}
          value={form.movieAgeLimit}
          onChange={(event) => set('movieAgeLimit', event.target.value)}
        />
      </label>
      <label>
        {t('admin.tvLimit')}
        <input
          type="number"
          min={0}
          max={21}
          value={form.tvAgeLimit}
          onChange={(event) => set('tvAgeLimit', event.target.value)}
        />
      </label>
      <label>
        {t('admin.plexLabel')}
        <input
          value={form.plexLabel}
          placeholder={form.name}
          onChange={(event) => set('plexLabel', event.target.value)}
        />
      </label>
      <PlexUserPicker
        value={form.plexUserId}
        onChange={(value) => set('plexUserId', value)}
      />
      <label>
        {t('admin.blockedGenres')}
        <input
          value={form.blockedGenres}
          placeholder={t('admin.blockedGenresHint')}
          onChange={(event) => set('blockedGenres', event.target.value)}
        />
      </label>
      <label>
        {t('admin.blockedKeywords')}
        <input
          value={form.blockedKeywords}
          placeholder={t('admin.blockedKeywordsHint')}
          onChange={(event) => set('blockedKeywords', event.target.value)}
        />
      </label>
      <label>
        {t('admin.allowedLanguages')}
        <input
          value={form.allowedLanguages}
          placeholder={t('admin.allowedLanguagesHint')}
          onChange={(event) => set('allowedLanguages', event.target.value)}
        />
      </label>
      <fieldset>
        <legend>{t('admin.homeRows')}</legend>
        <p className="muted">{t('admin.homeRowsHint')}</p>
        {options === null && !optionsFailed && (
          <p role="status">{t('admin.homeOptionsLoading')}</p>
        )}
        {optionsFailed && (
          <p role="alert">
            {t('admin.homeOptionsFailed')}{' '}
            <button type="button" onClick={onRetryOptions}>
              {t('admin.homeOptionsRetry')}
            </button>
          </p>
        )}
        {homeRows.map((choice, index) => {
          const name = homeRowName(choice.id, t)
          return (
            <div key={choice.id} className="home-row-choice">
              <label>
                <input
                  type="checkbox"
                  checked={choice.enabled}
                  onChange={() =>
                    set(
                      'homeRows',
                      homeRows.map((item, itemIndex) =>
                        itemIndex === index
                          ? { ...item, enabled: !item.enabled }
                          : item,
                      ),
                    )
                  }
                />
                {name}
              </label>
              <button
                type="button"
                aria-label={t('admin.moveUp', { name })}
                disabled={index === 0}
                onClick={() => moveHomeRow(index, -1)}
              >
                {t('admin.up')}
              </button>
              <button
                type="button"
                aria-label={t('admin.moveDown', { name })}
                disabled={index === homeRows.length - 1}
                onClick={() => moveHomeRow(index, 1)}
              >
                {t('admin.down')}
              </button>
            </div>
          )
        })}
        {pickable.length > 0 && (
          <label>
            {t('admin.addGenreRow')}
            <select
              value=""
              onChange={(event) => addGenreRow(event.target.value)}
            >
              <option value="">{t('admin.chooseGenre')}</option>
              {pickable.map((genre) => (
                <option key={genre.id} value={genre.id}>
                  {t(
                    genre.movie && genre.tv
                      ? 'admin.genreBoth'
                      : genre.tv
                        ? 'admin.genreShows'
                        : 'admin.genreMovies',
                    { name: genre.name },
                  )}
                </option>
              ))}
            </select>
          </label>
        )}
      </fieldset>
      <label>
        {t('admin.weekly')}
        <input
          type="number"
          min={0}
          value={form.weeklyAllowance}
          onChange={(event) => set('weeklyAllowance', event.target.value)}
        />
      </label>
      <label>
        <input
          type="checkbox"
          checked={form.autoApprove}
          onChange={(event) => set('autoApprove', event.target.checked)}
        />
        {t('admin.autoApprove')}
      </label>
      <p className="muted">{t('admin.autoApproveHint')}</p>
      <label>
        {t('admin.movieCeiling')}
        <select
          value={form.movieCeiling}
          onChange={(event) => set('movieCeiling', event.target.value)}
        >
          <option value="">{t('admin.ratingNone')}</option>
          {ratingLevels.map((level) => (
            <option key={level} value={level}>
              {t(ratingKey[level])}
            </option>
          ))}
        </select>
      </label>
      <label>
        {t('admin.tvCeiling')}
        <select
          value={form.tvCeiling}
          onChange={(event) => set('tvCeiling', event.target.value)}
        >
          <option value="">{t('admin.ratingNone')}</option>
          {ratingLevels.map((level) => (
            <option key={level} value={level}>
              {t(ratingKey[level])}
            </option>
          ))}
        </select>
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="submit" disabled={options === null}>
        {t('admin.save')}
      </button>
      <button type="button" onClick={onCancel}>
        {t('admin.cancel')}
      </button>
    </form>
  )
}

type DeleteConfirmProps = {
  profile: KidProfile
  onCancel: () => void
  onDeleted: () => void
}

function DeleteConfirm({ profile, onCancel, onDeleted }: DeleteConfirmProps) {
  const t = useT()
  const [removeLabels, setRemoveLabels] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function confirm() {
    setError(null)
    try {
      await deleteKidProfile(profile.id, removeLabels)
      onDeleted()
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    }
  }

  return (
    <div
      role="alertdialog"
      aria-label={t('admin.deleteDialog', { name: profile.name })}
    >
      <p>{t('admin.deleteQuestion', { name: profile.name })}</p>
      <label>
        <input
          type="checkbox"
          checked={removeLabels}
          onChange={(event) => setRemoveLabels(event.target.checked)}
        />
        {t('admin.removeLabels')}
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="button" onClick={() => void confirm()}>
        {t('admin.confirmDelete')}
      </button>
      <button type="button" onClick={onCancel}>
        {t('admin.keep')}
      </button>
    </div>
  )
}
